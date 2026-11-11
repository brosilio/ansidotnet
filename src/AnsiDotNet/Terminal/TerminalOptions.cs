using System;

namespace AnsiDotNet;

public enum MouseMode : byte
{
    None,
    /// <summary>Presses, releases and wheel.</summary>
    Buttons,
    /// <summary>Also motion while a button is held.</summary>
    Drag,
    /// <summary>All motion.</summary>
    AnyMotion,
}

/// <summary>Terminal setup applied by <see cref="Terminal"/> on open and undone on dispose.</summary>
public sealed class TerminalOptions
{
    public bool AlternateScreen { get; set; } = true;
    public bool HideCursor { get; set; } = true;
    public MouseMode Mouse { get; set; } = MouseMode.None;
    public bool BracketedPaste { get; set; } = true;
    public bool FocusEvents { get; set; }

    /// <summary>Kitty keyboard protocol (disambiguated keys) where supported; ignored elsewhere.</summary>
    public bool KittyKeyboard { get; set; }

    /// <summary>Turn off auto-wrap so writing the last column never scrolls.</summary>
    public bool DisableAutoWrap { get; set; } = true;

    public bool SynchronizedOutput { get; set; } = true;

    /// <summary>Null to detect from the environment (console) or assume true color (streams).</summary>
    public ColorMode? ColorMode { get; set; }

    public TextEncoding Encoding { get; set; } = TextEncoding.Utf8;

    /// <summary>How long a lone ESC waits for the rest of a sequence. Raise this for network links.</summary>
    public TimeSpan EscapeTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Local console only: deliver Ctrl+C (and Ctrl+Z, Ctrl+\) as keys rather than signals.</summary>
    public bool ControlCAsInput { get; set; } = true;

    /// <summary>Local console only: restore the terminal on process exit and unhandled exceptions.</summary>
    public bool RestoreOnExit { get; set; } = true;

    public string? Title { get; set; }
}
