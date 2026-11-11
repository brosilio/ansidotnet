using System;
using System.Text;

namespace AnsiDotNet;

public enum InputEventKind : byte
{
    None,
    Key,
    Mouse,
    Paste,
    Resize,
    FocusIn,
    FocusOut,
    /// <summary>Response to <see cref="AnsiWriter.RequestCursorPosition"/>; position in X/Y.</summary>
    CursorPosition,
    /// <summary>The input side has closed (EOF, disconnect). Returned repeatedly once reached.</summary>
    Closed,
    /// <summary>Posted by <see cref="Terminal.Wake"/> from another thread: something outside the terminal changed.</summary>
    Wake,
}

public enum Key : byte
{
    None,
    /// <summary>A printable character (or Ctrl/Alt + character); see <see cref="InputEvent.Rune"/>.</summary>
    Char,
    Enter,
    Escape,
    Backspace,
    Tab,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    KeypadBegin,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10,
    F11, F12, F13, F14, F15, F16, F17, F18, F19, F20,
}

[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
    Meta = 8,
}

public enum MouseButton : byte
{
    None,
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
    WheelLeft,
    WheelRight,
    Back,
    Forward,
}

public enum MouseAction : byte
{
    Press,
    Release,
    /// <summary>Motion with no button held (requires <see cref="MouseMode.AnyMotion"/>).</summary>
    Move,
    /// <summary>Motion with a button held.</summary>
    Drag,
    Wheel,
}

/// <summary>A decoded input event. Mouse and cursor positions are zero-based.</summary>
public readonly struct InputEvent : IEquatable<InputEvent>
{
    public InputEventKind Kind { get; init; }
    public Key Key { get; init; }
    public Rune Rune { get; init; }
    public KeyModifiers Modifiers { get; init; }
    public MouseButton Button { get; init; }
    public MouseAction Action { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>Pasted text for <see cref="InputEventKind.Paste"/>.</summary>
    public string? Text { get; init; }

    public int Width => X;
    public int Height => Y;

    public bool IsKey(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        Kind == InputEventKind.Key && Key == key && Modifiers == modifiers;

    /// <summary>True for a character key matching <paramref name="c"/> exactly (case-sensitive), with the given modifiers.</summary>
    public bool IsChar(char c, KeyModifiers modifiers = KeyModifiers.None) =>
        Kind == InputEventKind.Key && Key == Key.Char && Rune.Value == c && Modifiers == modifiers;

    /// <summary>Ctrl + letter, case-insensitive.</summary>
    public bool IsCtrl(char letter) =>
        Kind == InputEventKind.Key && Key == Key.Char && Modifiers == KeyModifiers.Control &&
        char.ToLowerInvariant((char)Rune.Value) == char.ToLowerInvariant(letter);

    public static InputEvent ForKey(Key key, KeyModifiers mods = KeyModifiers.None) =>
        new() { Kind = InputEventKind.Key, Key = key, Modifiers = mods };

    public static InputEvent ForChar(Rune rune, KeyModifiers mods = KeyModifiers.None) =>
        new() { Kind = InputEventKind.Key, Key = Key.Char, Rune = rune, Modifiers = mods };

    public static InputEvent ForMouse(MouseButton button, MouseAction action, int x, int y, KeyModifiers mods = KeyModifiers.None) =>
        new() { Kind = InputEventKind.Mouse, Button = button, Action = action, X = x, Y = y, Modifiers = mods };

    public static InputEvent ForResize(int width, int height) =>
        new() { Kind = InputEventKind.Resize, X = width, Y = height };

    public static InputEvent ForPaste(string text) => new() { Kind = InputEventKind.Paste, Text = text };

    public static InputEvent Simple(InputEventKind kind) => new() { Kind = kind };

    public bool Equals(InputEvent o) =>
        Kind == o.Kind && Key == o.Key && Rune == o.Rune && Modifiers == o.Modifiers && Button == o.Button &&
        Action == o.Action && X == o.X && Y == o.Y && Text == o.Text;

    public override bool Equals(object? obj) => obj is InputEvent e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(Kind, Key, Rune, Modifiers, Button, Action, X, Y);
    public static bool operator ==(InputEvent a, InputEvent b) => a.Equals(b);
    public static bool operator !=(InputEvent a, InputEvent b) => !a.Equals(b);

    public override string ToString()
    {
        string mods = FormatMods(Modifiers);
        return Kind switch
        {
            InputEventKind.Key when Key == Key.Char => $"Key {mods}{FormatRune(Rune)}",
            InputEventKind.Key => $"Key {mods}{Key}",
            InputEventKind.Mouse => $"Mouse {mods}{Button} {Action} @ {X},{Y}",
            InputEventKind.Paste => $"Paste ({Text?.Length ?? 0} chars)",
            InputEventKind.Resize => $"Resize {Width}x{Height}",
            InputEventKind.CursorPosition => $"CursorPosition {X},{Y}",
            _ => Kind.ToString(),
        };
    }

    private static string FormatRune(Rune r) => r.Value switch
    {
        ' ' => "Space",
        < 0x20 or 0x7F => $"U+{r.Value:X4}",
        _ => $"'{r}'",
    };

    private static string FormatMods(KeyModifiers m)
    {
        if (m == KeyModifiers.None)
            return "";
        var sb = new StringBuilder();
        if ((m & KeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((m & KeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((m & KeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((m & KeyModifiers.Meta) != 0) sb.Append("Meta+");
        return sb.ToString();
    }
}
