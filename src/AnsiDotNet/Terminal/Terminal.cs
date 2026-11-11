using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnsiDotNet;

/// <summary>
/// A terminal session over any <see cref="ITerminalBackend"/>: sets up modes, decodes input into an event queue,
/// and exposes buffered output and a diffing renderer. Dispose to restore the terminal.
/// </summary>
/// <remarks>
/// Input arrives on a background thread and is queued; read it from one thread at a time. <see cref="Output"/> and
/// <see cref="Renderer"/> are not thread-safe and should be used from a single thread.
/// </remarks>
public sealed class Terminal : IDisposable, ITerminalInput
{
    private static int _consoleOpen;

    private readonly ITerminalBackend _backend;
    private readonly TerminalOptions _options;
    private readonly InputParser _parser = new();
    private readonly Queue<InputEvent> _queue = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Timer _escapeTimer;
    private readonly bool _isConsole;
    private long _inputVersion, _timerVersion;
    private TerminalSize _size;
    private volatile bool _closed;
    private int _disposed;
    private int _wakePending;
    private bool _suspended;

    private Terminal(ITerminalBackend backend, TerminalOptions options, ColorMode colorMode, bool isConsole)
    {
        _backend = backend;
        _options = options;
        _isConsole = isConsole;
        _size = backend.Size;
        _parser.Encoding = options.Encoding;
        _escapeTimer = new Timer(static s => ((Terminal)s!).OnEscapeTimeout(), this, Timeout.Infinite, Timeout.Infinite);

        Output = new AnsiWriter(backend, 16 * 1024) { ColorMode = colorMode, Encoding = options.Encoding };
        Renderer = new ScreenRenderer(Output) { SynchronizedOutput = options.SynchronizedOutput };

        ApplyModes();
        Output.Flush();
        backend.Start(this);
    }

    /// <summary>Opens the process's own console. Only one may be open at a time.</summary>
    public static Terminal OpenConsole(TerminalOptions? options = null)
    {
        options ??= new TerminalOptions();
        if (Interlocked.Exchange(ref _consoleOpen, 1) != 0)
            throw new InvalidOperationException("The console terminal is already open.");
        try
        {
            ITerminalBackend backend = OperatingSystem.IsWindows()
                ? new WindowsConsoleBackend(options.ControlCAsInput)
                : new UnixConsoleBackend(options.ControlCAsInput);
            var colorMode = options.ColorMode ?? TerminalCapabilities.DetectColorMode();
            var term = new Terminal(backend, options, colorMode, isConsole: true);
            if (options.RestoreOnExit)
            {
                AppDomain.CurrentDomain.ProcessExit += term.OnProcessExit;
                AppDomain.CurrentDomain.UnhandledException += term.OnProcessExit;
            }
            return term;
        }
        catch
        {
            _consoleOpen = 0;
            throw;
        }
    }

    /// <summary>Opens a session over a custom backend, such as <see cref="StreamBackend"/> for a network client.</summary>
    public static Terminal Open(ITerminalBackend backend, TerminalOptions? options = null)
    {
        options ??= new TerminalOptions();
        return new Terminal(backend, options, options.ColorMode ?? ColorMode.TrueColor, isConsole: false);
    }

    public AnsiWriter Output { get; }
    public ScreenRenderer Renderer { get; }
    public ITerminalBackend Backend => _backend;

    public TerminalSize Size
    {
        get
        {
            lock (_lock)
                return _size;
        }
    }

    public int Width => Size.Width;
    public int Height => Size.Height;

    public bool IsClosed => _closed;

    public ColorMode ColorMode
    {
        get => Output.ColorMode;
        set
        {
            Output.ColorMode = value;
            Renderer.Invalidate();
        }
    }

    public TextEncoding Encoding
    {
        get => Output.Encoding;
        set
        {
            Output.Encoding = value;
            lock (_lock)
                _parser.Encoding = value;
            Renderer.Invalidate();
        }
    }

    /// <summary>Draws a frame with <see cref="Renderer"/> and flushes.</summary>
    public void Render(Surface frame) => Renderer.Render(frame);

    public void Flush() => Output.Flush();

    /// <summary>Asks the terminal where the cursor is; the answer arrives as a <see cref="InputEventKind.CursorPosition"/> event.</summary>
    public void RequestCursorPosition()
    {
        lock (_lock)
            _parser.ExpectCursorPositionReport();
        Output.RequestCursorPosition();
        Output.Flush();
    }

    #region Reading

    /// <summary>Returns a queued event without waiting.</summary>
    public bool TryReadEvent(out InputEvent ev) => TryDequeue(out ev);

    /// <summary>Waits up to <paramref name="timeout"/> for an event.</summary>
    public bool TryReadEvent(out InputEvent ev, TimeSpan timeout)
    {
        long deadline = Deadline(timeout);
        while (true)
        {
            if (TryDequeue(out ev))
                return true;
            int remaining = Remaining(deadline);
            if (remaining == 0)
                return false;
            if (!_signal.Wait(remaining))
                return TryDequeue(out ev);
        }
    }

    public async ValueTask<InputEvent> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryDequeue(out var ev))
                return ev;
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for an event; null on timeout.</summary>
    public async ValueTask<InputEvent?> ReadEventAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long deadline = Deadline(timeout);
        while (true)
        {
            if (TryDequeue(out var ev))
                return ev;
            int remaining = Remaining(deadline);
            if (remaining == 0)
                return null;
            if (!await _signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                return TryDequeue(out ev) ? ev : null;
        }
    }

    /// <summary>
    /// Makes a pending or future read return an <see cref="InputEventKind.Wake"/> event. Safe to call from any thread.
    /// Wakes coalesce: calls made before the reader sees the event produce only one. Use it to tell a session loop
    /// that shared state changed, instead of polling with a read timeout.
    /// </summary>
    public void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) != 0)
            return;
        lock (_lock)
            _queue.Enqueue(InputEvent.Simple(InputEventKind.Wake));
        _signal.Release();
    }

    private static long Deadline(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? -1 : Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);

    private static int Remaining(long deadline) =>
        deadline < 0 ? Timeout.Infinite : (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);

    private bool TryDequeue(out InputEvent ev)
    {
        lock (_lock)
        {
            if (_queue.TryDequeue(out ev))
            {
                if (ev.Kind == InputEventKind.Resize)
                    Renderer.Invalidate();
                else if (ev.Kind == InputEventKind.Wake)
                    Volatile.Write(ref _wakePending, 0);
                return true;
            }
        }
        if (_closed)
        {
            ev = InputEvent.Simple(InputEventKind.Closed);
            return true;
        }
        return false;
    }

    #endregion

    #region ITerminalInput

    void ITerminalInput.OnInput(ReadOnlySpan<byte> data)
    {
        int added;
        lock (_lock)
        {
            _parser.Feed(data);
            _inputVersion++;
            added = Drain();
            if (_parser.HasPendingInput)
            {
                _timerVersion = _inputVersion;
                _escapeTimer.Change(_options.EscapeTimeout, Timeout.InfiniteTimeSpan);
            }
        }
        if (added > 0)
            _signal.Release(added);
    }

    void ITerminalInput.OnResize(TerminalSize size)
    {
        lock (_lock)
        {
            if (size == _size)
                return;
            _size = size;
            _queue.Enqueue(InputEvent.ForResize(size.Width, size.Height));
        }
        _signal.Release();
    }

    void ITerminalInput.OnClosed()
    {
        _closed = true;
        _signal.Release();
    }

    private void OnEscapeTimeout()
    {
        int added = 0;
        lock (_lock)
        {
            if (_timerVersion == _inputVersion && _parser.HasPendingInput)
            {
                _parser.FlushPending();
                added = Drain();
            }
        }
        if (added > 0)
            _signal.Release(added);
    }

    private int Drain()
    {
        int n = 0;
        while (_parser.TryRead(out var ev))
        {
            _queue.Enqueue(ev);
            n++;
        }
        return n;
    }

    #endregion

    #region Modes

    private void ApplyModes()
    {
        var o = Output;
        var opt = _options;
        if (opt.AlternateScreen)
            o.EnterAlternateScreen();
        if (opt.HideCursor)
            o.HideCursor();
        if (opt.DisableAutoWrap)
            o.SetMode(PrivateMode.AutoWrap, false);
        if (opt.Mouse != MouseMode.None)
        {
            o.SetMode(MouseModeFor(opt.Mouse), true);
            o.SetMode(PrivateMode.MouseSgr, true);
        }
        if (opt.BracketedPaste)
            o.SetMode(PrivateMode.BracketedPaste, true);
        if (opt.FocusEvents)
            o.SetMode(PrivateMode.FocusEvents, true);
        if (opt.KittyKeyboard)
            o.PushKeyboardFlags(1);
        if (opt.Title is not null)
        {
            o.Raw("\e[22;0t"u8); // push title
            o.SetTitle(opt.Title);
        }
    }

    private void ResetModes()
    {
        var o = Output;
        var opt = _options;
        if (opt.Title is not null)
            o.Raw("\e[23;0t"u8);
        if (opt.KittyKeyboard)
            o.PopKeyboardFlags();
        if (opt.FocusEvents)
            o.SetMode(PrivateMode.FocusEvents, false);
        if (opt.BracketedPaste)
            o.SetMode(PrivateMode.BracketedPaste, false);
        if (opt.Mouse != MouseMode.None)
        {
            o.SetMode(PrivateMode.MouseSgr, false);
            o.SetMode(MouseModeFor(opt.Mouse), false);
        }
        if (Renderer.SynchronizedOutput)
            o.EndSynchronizedUpdate();
        o.SetMode(PrivateMode.AutoWrap, true);
        o.ResetStyle();
        o.ShowCursor();
        if (opt.AlternateScreen)
            o.LeaveAlternateScreen();
    }

    private static PrivateMode MouseModeFor(MouseMode m) => m switch
    {
        MouseMode.Drag => PrivateMode.MouseDrag,
        MouseMode.AnyMotion => PrivateMode.MouseAnyMotion,
        _ => PrivateMode.MouseButtons,
    };

    /// <summary>Restores the terminal's normal state (e.g. to launch an editor). Call <see cref="Resume"/> after.</summary>
    public void Suspend()
    {
        if (_suspended)
            return;
        _suspended = true;
        ResetModes();
        Output.Flush();
        _backend.Suspend();
    }

    public void Resume()
    {
        if (!_suspended)
            return;
        _suspended = false;
        _backend.Resume();
        ApplyModes();
        Renderer.Invalidate();
        Output.Flush();
    }

    #endregion

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_isConsole)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException -= OnProcessExit;
        }
        try
        {
            if (!_suspended)
                ResetModes();
            Output.Flush();
        }
        finally
        {
            _backend.Dispose();
            _escapeTimer.Dispose();
            _closed = true;
            _signal.Release();
            if (_isConsole)
                _consoleOpen = 0;
        }
    }
}
