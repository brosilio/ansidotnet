using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace AnsiDotNet;

[UnsupportedOSPlatform("windows")]
internal sealed unsafe class UnixConsoleBackend : ITerminalBackend
{
    private const int StdIn = 0, StdOut = 1, StdErr = 2;
    private const int TermiosSize = 256;

    private readonly bool _controlCAsInput;
    private readonly byte[] _original = new byte[TermiosSize];
    private readonly bool _haveOriginal;
    private readonly int _wakeRead = -1, _wakeWrite = -1;
    private ITerminalInput? _input;
    private Thread? _thread;
    private PosixSignalRegistration? _winch;
    private TerminalSize _size;
    private volatile bool _stop, _paused;
    private int _disposed;

    public UnixConsoleBackend(bool controlCAsInput)
    {
        _controlCAsInput = controlCAsInput;
        fixed (byte* t = _original)
            _haveOriginal = Libc.isatty(StdIn) == 1 && Libc.tcgetattr(StdIn, t) == 0;

        int* fds = stackalloc int[2];
        if (Libc.pipe(fds) == 0)
        {
            _wakeRead = fds[0];
            _wakeWrite = fds[1];
        }

        EnterRawMode();
        _size = QuerySize();
    }

    public TerminalSize Size => _size;

    public void Start(ITerminalInput input)
    {
        if (_input is not null)
            throw new InvalidOperationException("Already started.");
        _input = input;
        _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => Wake());
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "AnsiDotNet console input" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        var fds = stackalloc Libc.PollFd[2];
        byte* junk = stackalloc byte[64];
        while (!_stop)
        {
            bool paused = _paused;
            int n = 0;
            if (!paused)
                fds[n++] = new Libc.PollFd { Fd = StdIn, Events = Libc.POLLIN };
            if (_wakeRead >= 0)
                fds[n++] = new Libc.PollFd { Fd = _wakeRead, Events = Libc.POLLIN };

            // Without a wake pipe, fall back to polling so resizes and Dispose are still noticed.
            int r = Libc.poll(fds, (nuint)n, _wakeRead >= 0 ? -1 : 100);
            if (r < 0)
            {
                if (Marshal.GetLastPInvokeError() == Libc.EINTR)
                    continue;
                break;
            }

            if (_wakeRead >= 0 && (fds[n - 1].REvents & Libc.POLLIN) != 0)
            {
                Libc.read(_wakeRead, junk, 64);
            }
            if (_stop)
                break;

            var size = QuerySize();
            if (size != _size)
            {
                _size = size;
                _input!.OnResize(size);
            }

            if (paused || (fds[0].REvents & (Libc.POLLIN | Libc.POLLHUP | Libc.POLLERR | Libc.POLLNVAL)) == 0)
                continue;

            nint got;
            fixed (byte* p = buf)
                got = Libc.read(StdIn, p, buf.Length);
            if (got > 0)
            {
                _input!.OnInput(buf.AsSpan(0, (int)got));
            }
            else if (got == 0 || !IsTransient(Marshal.GetLastPInvokeError()))
            {
                _input!.OnClosed();
                break;
            }
        }
    }

    private static bool IsTransient(int errno) =>
        errno == Libc.EINTR || errno == Libc.EAGAIN_LINUX || errno == Libc.EAGAIN_BSD;

    private void Wake()
    {
        if (_wakeWrite < 0)
            return;
        byte b = 1;
        Libc.write(_wakeWrite, &b, 1);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* start = data)
        {
            byte* p = start;
            nint left = data.Length;
            while (left > 0)
            {
                nint n = Libc.write(StdOut, p, left);
                if (n > 0)
                {
                    p += n;
                    left -= n;
                    continue;
                }
                int err = Marshal.GetLastPInvokeError();
                if (err == Libc.EINTR)
                    continue;
                if (err == Libc.EAGAIN_LINUX || err == Libc.EAGAIN_BSD)
                {
                    var pfd = new Libc.PollFd { Fd = StdOut, Events = Libc.POLLOUT };
                    Libc.poll(&pfd, 1, 100);
                    continue;
                }
                return; // EPIPE etc: nothing sensible to do with the output
            }
        }
    }

    public void Suspend()
    {
        _paused = true;
        Wake();
        RestoreMode();
    }

    public void Resume()
    {
        EnterRawMode();
        _paused = false;
        Wake();
    }

    private void EnterRawMode()
    {
        if (!_haveOriginal)
            return;
        byte* raw = stackalloc byte[TermiosSize];
        _original.AsSpan().CopyTo(new Span<byte>(raw, TermiosSize));
        Libc.cfmakeraw(raw);

        // cfmakeraw also disables output post-processing; keep it so "\n" still returns the carriage.
        bool wide = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        int flagSize = wide ? 8 : 4;
        *(uint*)(raw + flagSize) |= 0x1; // c_oflag |= OPOST
        if (!_controlCAsInput)
            *(uint*)(raw + flagSize * 3) |= wide || OperatingSystem.IsFreeBSD() ? 0x80u : 0x1u; // c_lflag |= ISIG

        Libc.tcsetattr(StdIn, Libc.TCSANOW, raw);
    }

    private void RestoreMode()
    {
        if (!_haveOriginal)
            return;
        fixed (byte* t = _original)
            Libc.tcsetattr(StdIn, Libc.TCSADRAIN, t);
    }

    private static TerminalSize QuerySize()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            nuint req = OperatingSystem.IsLinux() ? 0x5413u : 0x40087468u;
            Libc.WinSize ws;
            foreach (int fd in (ReadOnlySpan<int>)[StdOut, StdIn, StdErr])
            {
                if (Libc.ioctl(fd, req, &ws) == 0 && ws.Cols > 0 && ws.Rows > 0)
                    return new TerminalSize(ws.Cols, ws.Rows);
            }
        }
        else
        {
            // ioctl is variadic, which Apple's arm64 ABI passes differently; let the runtime ask instead.
            try
            {
                int w = Console.WindowWidth, h = Console.WindowHeight;
                if (w > 0 && h > 0)
                    return new TerminalSize(w, h);
            }
            catch (Exception e) when (e is System.IO.IOException or PlatformNotSupportedException)
            {
            }
        }
        return TerminalCapabilities.EnvironmentSize();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        Wake();
        _winch?.Dispose();
        if (_thread is not null && _thread != Thread.CurrentThread)
            _thread.Join(500);
        RestoreMode();
        if (_wakeRead >= 0)
            Libc.close(_wakeRead);
        if (_wakeWrite >= 0)
            Libc.close(_wakeWrite);
    }
}
