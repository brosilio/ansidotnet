using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace AnsiDotNet;

[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsConsoleBackend : ITerminalBackend
{
    private readonly nint _in, _out;
    private readonly bool _consoleIn, _consoleOut;
    private readonly uint _originalIn, _originalOut, _originalCodePage;
    private readonly bool _controlCAsInput;
    private ITerminalInput? _input;
    private Thread? _thread;
    private TerminalSize _size;
    private volatile bool _stop, _paused;
    private int _disposed;

    public WindowsConsoleBackend(bool controlCAsInput)
    {
        _controlCAsInput = controlCAsInput;
        _in = Kernel32.GetStdHandle(Kernel32.STD_INPUT_HANDLE);
        _out = Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE);
        uint mode;
        _consoleIn = Kernel32.GetConsoleMode(_in, &mode) != 0;
        _originalIn = mode;
        _consoleOut = Kernel32.GetConsoleMode(_out, &mode) != 0;
        _originalOut = mode;
        _originalCodePage = Kernel32.GetConsoleOutputCP();
        EnterRawMode();
        _size = QuerySize();
    }

    public TerminalSize Size => _size;

    public void Start(ITerminalInput input)
    {
        if (_input is not null)
            throw new InvalidOperationException("Already started.");
        _input = input;
        _thread = new Thread(_consoleIn ? ConsoleReadLoop : StreamReadLoop)
        {
            IsBackground = true,
            Name = "AnsiDotNet console input",
        };
        _thread.Start();
    }

    private void ConsoleReadLoop()
    {
        var records = new Kernel32.InputRecord[64];
        var chars = new char[1024];
        var bytes = new byte[4096];
        var encoder = new UTF8Encoding(false).GetEncoder(); // stateful across split surrogate pairs

        while (!_stop)
        {
            if (_paused)
            {
                Thread.Sleep(50);
                continue;
            }
            uint wait = Kernel32.WaitForSingleObject(_in, 50);
            CheckSize();
            if (wait != Kernel32.WAIT_OBJECT_0 || _stop || _paused)
                continue;

            // ReadConsoleW blocks unless a character-producing key-down record is queued, so look first.
            uint count;
            bool hasText = false;
            fixed (Kernel32.InputRecord* r = records)
            {
                if (Kernel32.PeekConsoleInputW(_in, r, (uint)records.Length, &count) == 0)
                    continue;
                for (int i = 0; i < count; i++)
                {
                    if (r[i].EventType == Kernel32.KEY_EVENT && r[i].KeyDown != 0 && r[i].UnicodeChar != 0)
                    {
                        hasText = true;
                        break;
                    }
                }
                if (!hasText)
                {
                    Kernel32.ReadConsoleInputW(_in, r, count, &count);
                    continue;
                }
            }

            uint read;
            fixed (char* c = chars)
            {
                if (Kernel32.ReadConsoleW(_in, c, (uint)chars.Length, &read, null) == 0)
                    break;
            }
            if (read == 0)
                continue;
            int n = encoder.GetBytes(chars, 0, (int)read, bytes, 0, false);
            if (n > 0)
                _input!.OnInput(bytes.AsSpan(0, n));
        }
    }

    private void StreamReadLoop()
    {
        using var stdin = Console.OpenStandardInput();
        var buf = new byte[4096];
        try
        {
            int n;
            while (!_stop && (n = stdin.Read(buf, 0, buf.Length)) > 0)
            {
                if (!_paused)
                    _input!.OnInput(buf.AsSpan(0, n));
            }
        }
        catch (IOException)
        {
        }
        _input!.OnClosed();
    }

    private void CheckSize()
    {
        var size = QuerySize();
        if (size != _size)
        {
            _size = size;
            _input!.OnResize(size);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* start = data)
        {
            byte* p = start;
            int left = data.Length;
            while (left > 0)
            {
                uint written;
                if (Kernel32.WriteFile(_out, p, (uint)left, &written, null) == 0 || written == 0)
                    return;
                p += written;
                left -= (int)written;
            }
        }
    }

    public void Suspend()
    {
        _paused = true;
        RestoreMode();
    }

    public void Resume()
    {
        EnterRawMode();
        _paused = false;
    }

    private void EnterRawMode()
    {
        if (_consoleIn)
        {
            uint mode = Kernel32.ENABLE_VIRTUAL_TERMINAL_INPUT | Kernel32.ENABLE_WINDOW_INPUT | Kernel32.ENABLE_EXTENDED_FLAGS;
            if (!_controlCAsInput)
                mode |= Kernel32.ENABLE_PROCESSED_INPUT;
            Kernel32.SetConsoleMode(_in, mode);
        }
        if (_consoleOut)
        {
            Kernel32.SetConsoleMode(_out, _originalOut | Kernel32.ENABLE_PROCESSED_OUTPUT | Kernel32.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            Kernel32.SetConsoleOutputCP(65001);
        }
    }

    private void RestoreMode()
    {
        if (_consoleIn)
            Kernel32.SetConsoleMode(_in, _originalIn);
        if (_consoleOut)
        {
            Kernel32.SetConsoleMode(_out, _originalOut);
            Kernel32.SetConsoleOutputCP(_originalCodePage);
        }
    }

    private TerminalSize QuerySize()
    {
        Kernel32.ScreenBufferInfo info;
        if (_consoleOut && Kernel32.GetConsoleScreenBufferInfo(_out, &info) != 0)
        {
            int w = info.Right - info.Left + 1, h = info.Bottom - info.Top + 1;
            if (w > 0 && h > 0)
                return new TerminalSize(w, h);
        }
        return TerminalCapabilities.EnvironmentSize();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop = true;
        if (_consoleIn && _thread is not null && _thread != Thread.CurrentThread)
            _thread.Join(500);
        RestoreMode();
    }
}
