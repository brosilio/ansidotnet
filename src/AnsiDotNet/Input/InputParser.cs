using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace AnsiDotNet;

/// <summary>
/// Incremental decoder turning raw terminal input bytes into <see cref="InputEvent"/>s. Transport-agnostic:
/// feed it bytes from stdin, a socket, a pipe. Handles xterm/VT keys, SS3, kitty CSI-u, modifyOtherKeys,
/// SGR/X10/urxvt mouse, bracketed paste and focus events. Not thread-safe.
/// </summary>
/// <remarks>
/// A lone ESC is ambiguous until more bytes arrive. When <see cref="HasPendingInput"/> stays true for a short
/// while with no new data, call <see cref="FlushPending"/> to resolve it as a key press.
/// </remarks>
public sealed class InputParser
{
    private const int MaxSequence = 128;
    private const int MaxString = 4096;
    private const int MaxPaste = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> PasteEnd => "\e[201~"u8;

    private readonly Queue<InputEvent> _events = new();
    private byte[] _buf = new byte[256];
    private int _len;
    private bool _inPaste;
    private ArrayBufferWriter<byte>? _paste;
    private bool _afterCr;
    private int _expectCursorReports;

    public TextEncoding Encoding { get; set; } = TextEncoding.Utf8;

    /// <summary>Decoded events waiting to be read.</summary>
    public int Count => _events.Count;

    /// <summary>True when buffered bytes form an incomplete sequence (and not merely an unfinished paste).</summary>
    public bool HasPendingInput => _len > 0 && !_inPaste;

    /// <summary>
    /// <c>CSI row;col R</c> is both a cursor report and modified F3. Call this when you request a report so the
    /// next one is decoded as <see cref="InputEventKind.CursorPosition"/>.
    /// </summary>
    public void ExpectCursorPositionReport() => _expectCursorReports++;

    public bool TryRead(out InputEvent ev) => _events.TryDequeue(out ev);

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        if (_buf.Length - _len < data.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Process(false);
    }

    /// <summary>Resolves any incomplete sequence as literal keys (ESC becomes Escape, ESC x becomes Alt+x).</summary>
    public void FlushPending()
    {
        if (_len > 0)
            Process(true);
    }

    public void Reset()
    {
        _events.Clear();
        _len = 0;
        _inPaste = false;
        _paste?.Clear();
        _afterCr = false;
        _expectCursorReports = 0;
    }

    private void Process(bool force)
    {
        int i = 0;
        while (i < _len)
        {
            var s = new ReadOnlySpan<byte>(_buf, i, _len - i);
            int n = _inPaste ? ConsumePaste(s) : ParseOne(s, force);
            if (n == 0)
                break;
            i += n;
        }
        if (i > 0)
        {
            Buffer.BlockCopy(_buf, i, _buf, 0, _len - i);
            _len -= i;
        }
    }

    private void Emit(in InputEvent ev) => _events.Enqueue(ev);

    private int ConsumePaste(ReadOnlySpan<byte> s)
    {
        _paste ??= new ArrayBufferWriter<byte>();
        int end = s.IndexOf(PasteEnd);
        if (end >= 0)
        {
            AppendPaste(s[..end]);
            _inPaste = false;
            Emit(InputEvent.ForPaste(DecodePaste()));
            _paste.Clear();
            return end + PasteEnd.Length;
        }

        // Hold back a tail that might be the start of the terminator.
        int keep = Math.Min(s.Length, PasteEnd.Length - 1);
        while (keep > 0 && !PasteEnd.StartsWith(s[^keep..]))
            keep--;
        int take = s.Length - keep;
        AppendPaste(s[..take]);
        return take;
    }

    private void AppendPaste(ReadOnlySpan<byte> data)
    {
        if (_paste!.WrittenCount + data.Length <= MaxPaste)
            _paste.Write(data);
    }

    private string DecodePaste()
    {
        var bytes = _paste!.WrittenSpan;
        string text = Encoding == TextEncoding.Utf8 ? System.Text.Encoding.UTF8.GetString(bytes) : Cp437.Decode(bytes);
        return text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    private int ParseOne(ReadOnlySpan<byte> s, bool force)
    {
        byte b = s[0];
        if (b == 0x1B)
        {
            _afterCr = false;
            return ParseEscape(s, force);
        }

        // Telnet clients often send CR LF or CR NUL for Enter.
        if (_afterCr)
        {
            _afterCr = false;
            if (b == 0x0A || b == 0x00)
                return 1;
        }

        int n = ParsePlain(s, force, out var ev);
        if (n > 0)
        {
            _afterCr = b == 0x0D;
            Emit(ev);
        }
        return n;
    }

    private int ParsePlain(ReadOnlySpan<byte> s, bool force, out InputEvent ev)
    {
        byte b = s[0];
        switch (b)
        {
            case 0x0D:
            case 0x0A:
                ev = InputEvent.ForKey(Key.Enter);
                return 1;
            case 0x09:
                ev = InputEvent.ForKey(Key.Tab);
                return 1;
            case 0x7F:
            case 0x08:
                ev = InputEvent.ForKey(Key.Backspace);
                return 1;
            case 0x00:
                ev = InputEvent.ForChar(new Rune(' '), KeyModifiers.Control);
                return 1;
            case 0x1B:
                ev = InputEvent.ForKey(Key.Escape);
                return 1;
        }
        if (b < 0x20)
        {
            char c = b <= 0x1A ? (char)(b + 0x60) : (char)(b + 0x40);
            ev = InputEvent.ForChar(new Rune(c), KeyModifiers.Control);
            return 1;
        }
        if (b < 0x80 || Encoding == TextEncoding.Ascii)
        {
            ev = InputEvent.ForChar(new Rune(b < 0x80 ? b : '?'));
            return 1;
        }
        if (Encoding == TextEncoding.Cp437)
        {
            ev = InputEvent.ForChar(new Rune(Cp437.Decode(b)));
            return 1;
        }

        var status = Rune.DecodeFromUtf8(s, out var rune, out int consumed);
        if (status == OperationStatus.NeedMoreData && !force)
        {
            ev = default;
            return 0;
        }
        if (status != OperationStatus.Done)
            rune = Rune.ReplacementChar;
        ev = InputEvent.ForChar(rune);
        return Math.Max(1, consumed);
    }

    private int ParseEscape(ReadOnlySpan<byte> s, bool force)
    {
        if (s.Length == 1)
        {
            if (!force)
                return 0;
            Emit(InputEvent.ForKey(Key.Escape));
            return 1;
        }

        byte n = s[1];
        switch (n)
        {
            case (byte)'[':
                return ParseCsi(s, force);
            case (byte)'O':
                if (s.Length < 3)
                    return force ? AltChar('O') : 0;
                ParseSs3(s[2]);
                return 3;
            case (byte)']':
            case (byte)'P':
            case (byte)'_':
            case (byte)'^':
                return SkipString(s, force, (char)n);
            case 0x1B:
                Emit(InputEvent.ForKey(Key.Escape));
                return 1;
        }

        int len = ParsePlain(s[1..], force, out var ev);
        if (len == 0)
        {
            if (!force)
                return 0;
            Emit(InputEvent.ForKey(Key.Escape));
            return 1;
        }
        Emit(ev with { Modifiers = ev.Modifiers | KeyModifiers.Alt });
        return 1 + len;
    }

    private int AltChar(char c)
    {
        Emit(InputEvent.ForChar(new Rune(c), KeyModifiers.Alt));
        return 2;
    }

    // OSC/DCS/APC/PM replies (e.g. color queries) are consumed and ignored.
    private int SkipString(ReadOnlySpan<byte> s, bool force, char intro)
    {
        for (int i = 2; i < s.Length && i < MaxString; i++)
        {
            if (s[i] == 0x07)
                return i + 1;
            if (s[i] == 0x1B && i + 1 < s.Length && s[i + 1] == (byte)'\\')
                return i + 2;
        }
        if (s.Length >= MaxString || force)
            return AltChar(intro);
        return 0;
    }

    private void ParseSs3(byte c)
    {
        Key key = c switch
        {
            (byte)'A' => Key.Up,
            (byte)'B' => Key.Down,
            (byte)'C' => Key.Right,
            (byte)'D' => Key.Left,
            (byte)'H' => Key.Home,
            (byte)'F' => Key.End,
            (byte)'E' => Key.KeypadBegin,
            (byte)'P' => Key.F1,
            (byte)'Q' => Key.F2,
            (byte)'R' => Key.F3,
            (byte)'S' => Key.F4,
            (byte)'M' => Key.Enter,
            _ => Key.None,
        };
        if (key != Key.None)
        {
            Emit(InputEvent.ForKey(key));
            return;
        }
        // Application keypad
        const string keypad = "*+,-./0123456789";
        if (c >= (byte)'j' && c <= (byte)'y')
            Emit(InputEvent.ForChar(new Rune(keypad[c - 'j'])));
        else if (c == (byte)'X')
            Emit(InputEvent.ForChar(new Rune('=')));
    }

    private int ParseCsi(ReadOnlySpan<byte> s, bool force)
    {
        // Linux console F1-F5: ESC [ [ A..E
        if (s.Length >= 3 && s[2] == (byte)'[')
        {
            if (s.Length < 4)
                return force ? AltChar('[') : 0;
            if (s[3] >= (byte)'A' && s[3] <= (byte)'E')
                Emit(InputEvent.ForKey((Key)((int)Key.F1 + s[3] - 'A')));
            return 4;
        }

        int i = 2;
        byte prefix = 0;
        if (i < s.Length && s[i] is (byte)'<' or (byte)'=' or (byte)'>' or (byte)'?')
            prefix = s[i++];

        Span<int> p = stackalloc int[16];
        int count = 0, cur = 0;
        bool any = false, sub = false;

        for (; i < s.Length && i < MaxSequence; i++)
        {
            byte c = s[i];
            if (c >= (byte)'0' && c <= (byte)'9')
            {
                if (!sub && cur < 100_000)
                    cur = cur * 10 + (c - '0');
                any = true;
            }
            else if (c == (byte)';')
            {
                if (count < p.Length)
                    p[count++] = cur;
                cur = 0;
                any = true;
                sub = false;
            }
            else if (c == (byte)':')
            {
                sub = true;
            }
            else if (c >= 0x20 && c <= 0x2F)
            {
                // intermediate bytes: not used by any input sequence we care about
            }
            else if (c >= 0x40 && c <= 0x7E)
            {
                if (any && count < p.Length)
                    p[count++] = cur;

                if (c == (byte)'M' && prefix == 0 && i == 2)
                {
                    if (s.Length < 6)
                        return force ? AltChar('[') : 0;
                    LegacyMouse(s[3] - 32, s[4] - 33, s[5] - 33);
                    return 6;
                }

                DispatchCsi(prefix, p[..count], (char)c);
                return i + 1;
            }
            else
            {
                // Malformed; drop the introducer and reparse the rest as plain input.
                return 2;
            }
        }

        if (force || s.Length >= MaxSequence)
            return AltChar('[');
        return 0;
    }

    private static int Param(ReadOnlySpan<int> p, int index, int fallback) =>
        index < p.Length && p[index] != 0 ? p[index] : fallback;

    private static KeyModifiers Mods(int v) => v <= 1 ? KeyModifiers.None : (KeyModifiers)((v - 1) & 0xF);

    private void DispatchCsi(byte prefix, ReadOnlySpan<int> p, char final)
    {
        if (prefix == (byte)'<')
        {
            if ((final == 'M' || final == 'm') && p.Length >= 3)
                SgrMouse(p[0], p[1] - 1, p[2] - 1, final == 'm');
            return;
        }
        if (prefix != 0)
            return; // device attribute / mode reports

        var mods = Mods(Param(p, 1, 1));
        switch (final)
        {
            case 'A': Emit(InputEvent.ForKey(Key.Up, mods)); break;
            case 'B': Emit(InputEvent.ForKey(Key.Down, mods)); break;
            case 'C': Emit(InputEvent.ForKey(Key.Right, mods)); break;
            case 'D': Emit(InputEvent.ForKey(Key.Left, mods)); break;
            case 'H': Emit(InputEvent.ForKey(Key.Home, mods)); break;
            case 'F': Emit(InputEvent.ForKey(Key.End, mods)); break;
            case 'E': Emit(InputEvent.ForKey(Key.KeypadBegin, mods)); break;
            case 'P': Emit(InputEvent.ForKey(Key.F1, mods)); break;
            case 'Q': Emit(InputEvent.ForKey(Key.F2, mods)); break;
            case 'S': Emit(InputEvent.ForKey(Key.F4, mods)); break;
            case 'R':
                if (_expectCursorReports > 0 && p.Length >= 2)
                {
                    _expectCursorReports--;
                    Emit(new InputEvent { Kind = InputEventKind.CursorPosition, X = p[1] - 1, Y = p[0] - 1 });
                }
                else
                {
                    Emit(InputEvent.ForKey(Key.F3, mods));
                }
                break;
            case 'Z': Emit(InputEvent.ForKey(Key.Tab, mods | KeyModifiers.Shift)); break;
            case 'I': Emit(InputEvent.Simple(InputEventKind.FocusIn)); break;
            case 'O': Emit(InputEvent.Simple(InputEventKind.FocusOut)); break;
            case 'M':
                if (p.Length >= 3)
                    LegacyMouse(p[0] - 32, p[1] - 1, p[2] - 1); // urxvt 1015
                break;
            case 'u':
                EmitCodepoint(Param(p, 0, 0), mods);
                break;
            case '~':
                Tilde(p, mods);
                break;
        }
    }

    private void Tilde(ReadOnlySpan<int> p, KeyModifiers mods)
    {
        int code = Param(p, 0, 0);
        Key key = code switch
        {
            1 or 7 => Key.Home,
            2 => Key.Insert,
            3 => Key.Delete,
            4 or 8 => Key.End,
            5 => Key.PageUp,
            6 => Key.PageDown,
            >= 11 and <= 15 => (Key)((int)Key.F1 + code - 11),
            >= 17 and <= 21 => (Key)((int)Key.F6 + code - 17),
            23 or 24 => (Key)((int)Key.F11 + code - 23),
            25 or 26 => (Key)((int)Key.F13 + code - 25),
            28 or 29 => (Key)((int)Key.F15 + code - 28),
            >= 31 and <= 34 => (Key)((int)Key.F17 + code - 31),
            _ => Key.None,
        };
        if (key != Key.None)
        {
            Emit(InputEvent.ForKey(key, mods));
            return;
        }
        switch (code)
        {
            case 27: // xterm modifyOtherKeys: CSI 27 ; mods ; code ~
                EmitCodepoint(Param(p, 2, 0), mods);
                break;
            case 200:
                _inPaste = true;
                _paste?.Clear();
                break;
        }
    }

    private void EmitCodepoint(int code, KeyModifiers mods)
    {
        switch (code)
        {
            case 13: Emit(InputEvent.ForKey(Key.Enter, mods)); return;
            case 9: Emit(InputEvent.ForKey(Key.Tab, mods)); return;
            case 27: Emit(InputEvent.ForKey(Key.Escape, mods)); return;
            case 8:
            case 127: Emit(InputEvent.ForKey(Key.Backspace, mods)); return;
        }
        // Kitty puts functional keys in the private use area; we don't map those.
        if (code >= 0x20 && code < 0xE000 && Rune.IsValid(code))
            Emit(InputEvent.ForChar(new Rune(code), mods));
    }

    private void SgrMouse(int cb, int x, int y, bool release)
    {
        var mods = MouseMods(cb);
        bool motion = (cb & 32) != 0;
        int low = cb & 3;
        MouseButton button;
        MouseAction action;
        if ((cb & 64) != 0)
        {
            button = (MouseButton)((int)MouseButton.WheelUp + low);
            action = MouseAction.Wheel;
        }
        else
        {
            button = (cb & 128) != 0
                ? low switch { 0 => MouseButton.Back, 1 => MouseButton.Forward, _ => MouseButton.None }
                : low switch { 0 => MouseButton.Left, 1 => MouseButton.Middle, 2 => MouseButton.Right, _ => MouseButton.None };
            action = release ? MouseAction.Release
                : motion ? (button == MouseButton.None ? MouseAction.Move : MouseAction.Drag)
                : MouseAction.Press;
        }
        Emit(InputEvent.ForMouse(button, action, Math.Max(0, x), Math.Max(0, y), mods));
    }

    private void LegacyMouse(int cb, int x, int y)
    {
        if (cb < 0)
            return;
        if ((cb & 3) == 3 && (cb & 64) == 0)
        {
            bool motion = (cb & 32) != 0;
            Emit(InputEvent.ForMouse(MouseButton.None, motion ? MouseAction.Move : MouseAction.Release,
                Math.Max(0, x), Math.Max(0, y), MouseMods(cb)));
            return;
        }
        SgrMouse(cb, x, y, false);
    }

    private static KeyModifiers MouseMods(int cb)
    {
        var m = KeyModifiers.None;
        if ((cb & 4) != 0) m |= KeyModifiers.Shift;
        if ((cb & 8) != 0) m |= KeyModifiers.Alt;
        if ((cb & 16) != 0) m |= KeyModifiers.Control;
        return m;
    }
}
