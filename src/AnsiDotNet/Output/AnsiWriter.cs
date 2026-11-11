using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AnsiDotNet;

/// <summary>Destination for encoded terminal output.</summary>
public interface IOutputSink
{
    void Write(ReadOnlySpan<byte> data);
}

public enum CursorShape
{
    Default = 0,
    BlinkingBlock = 1,
    Block = 2,
    BlinkingUnderline = 3,
    Underline = 4,
    BlinkingBar = 5,
    Bar = 6,
}

/// <summary>DEC private modes, set with <c>CSI ? n h</c> and reset with <c>CSI ? n l</c>.</summary>
public enum PrivateMode
{
    ApplicationCursorKeys = 1,
    AutoWrap = 7,
    MouseX10 = 9,
    CursorBlink = 12,
    CursorVisible = 25,
    MouseButtons = 1000,
    MouseDrag = 1002,
    MouseAnyMotion = 1003,
    FocusEvents = 1004,
    MouseUtf8 = 1005,
    MouseSgr = 1006,
    AlternateScreen = 1049,
    BracketedPaste = 2004,
    SynchronizedOutput = 2026,
}

/// <summary>
/// Buffers text and escape sequences as encoded bytes and hands them to a sink on <see cref="Flush"/>.
/// Coordinates are zero-based (x = column, y = row). Not thread-safe.
/// </summary>
public sealed class AnsiWriter : IBufferWriter<byte>
{
    private byte[] _buffer;
    private int _pos;
    private int _precision = 8;
    private readonly IOutputSink? _sink;

    public AnsiWriter(IOutputSink? sink = null, int initialCapacity = 4096)
    {
        _sink = sink;
        _buffer = new byte[Math.Max(64, initialCapacity)];
    }

    public AnsiWriter(Stream stream, int initialCapacity = 4096) : this(new StreamSink(stream), initialCapacity) { }

    /// <summary>Colors are downsampled to this mode as they are written.</summary>
    public ColorMode ColorMode { get; set; } = ColorMode.TrueColor;

    public TextEncoding Encoding { get; set; } = TextEncoding.Utf8;

    /// <summary>
    /// Emit bright colors as bold (foreground) and blink (background, "iCE color") instead of the 90-107 range.
    /// For ANSI.SYS-era clients.
    /// </summary>
    public bool LegacyBrightColors { get; set; }

    /// <summary>
    /// Bits per channel for 24-bit colors (1-8, default 8 = exact). Lower values round nearby colors together, so
    /// smooth gradients repeat the previous SGR more often. At 5 or 6 bits, full-screen animation often needs
    /// half the bytes or fewer, with little visible difference.
    /// </summary>
    public int TrueColorPrecision
    {
        get => _precision;
        set => _precision = Math.Clamp(value, 1, 8);
    }

    /// <summary>Bytes currently buffered.</summary>
    public int Length => _pos;

    public long TotalBytesFlushed { get; private set; }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _pos);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _pos);

    public void Clear() => _pos = 0;

    /// <summary>Drops buffered bytes past <paramref name="length"/> (a position previously read from <see cref="Length"/>).</summary>
    public void Truncate(int length) => _pos = Math.Clamp(length, 0, _pos);

    /// <summary>Sends buffered bytes to the sink (if any) and clears the buffer.</summary>
    public void Flush()
    {
        if (_pos == 0)
            return;
        if (_sink is null)
            throw new InvalidOperationException("No sink configured; use FlushTo or WrittenSpan.");
        _sink.Write(_buffer.AsSpan(0, _pos));
        TotalBytesFlushed += _pos;
        _pos = 0;
    }

    public void FlushTo(Stream stream)
    {
        stream.Write(_buffer, 0, _pos);
        TotalBytesFlushed += _pos;
        _pos = 0;
    }

    public void FlushTo(IOutputSink sink)
    {
        sink.Write(_buffer.AsSpan(0, _pos));
        TotalBytesFlushed += _pos;
        _pos = 0;
    }

    #region IBufferWriter

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _pos))
            throw new ArgumentOutOfRangeException(nameof(count));
        _pos += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsMemory(_pos);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsSpan(_pos);
    }

    #endregion

    #region Text

    public AnsiWriter Write(string? text) => text is null ? this : Write(text.AsSpan());

    public AnsiWriter Write(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return this;

        // ASCII fast path, shared by every encoding.
        Ensure(text.Length);
        int i = 0;
        var buf = _buffer;
        int pos = _pos;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x80)
                break;
            buf[pos++] = (byte)c;
        }
        _pos = pos;
        if (i == text.Length)
            return this;

        var rest = text[i..];
        switch (Encoding)
        {
            case TextEncoding.Utf8:
                Ensure(System.Text.Encoding.UTF8.GetMaxByteCount(rest.Length));
                _pos += System.Text.Encoding.UTF8.GetBytes(rest, _buffer.AsSpan(_pos));
                break;
            default:
                Ensure(rest.Length);
                foreach (var r in rest.EnumerateRunes())
                    _buffer[_pos++] = EncodeSingleByte(r);
                break;
        }
        return this;
    }

    public AnsiWriter Write(char c)
    {
        if (c < 0x80)
        {
            Ensure(1);
            _buffer[_pos++] = (byte)c;
            return this;
        }
        return Write(Rune.TryCreate(c, out var r) ? r : Rune.ReplacementChar);
    }

    public AnsiWriter Write(Rune rune)
    {
        int v = rune.Value;
        if (v < 0x80)
        {
            Ensure(1);
            _buffer[_pos++] = (byte)v;
            return this;
        }
        if (Encoding == TextEncoding.Utf8)
        {
            Ensure(4);
            _pos += rune.EncodeToUtf8(_buffer.AsSpan(_pos));
        }
        else
        {
            Ensure(1);
            _buffer[_pos++] = EncodeSingleByte(rune);
        }
        return this;
    }

    /// <summary>Writes text with a style, then resets attributes.</summary>
    public AnsiWriter Write(ReadOnlySpan<char> text, in Style style)
    {
        SetStyle(style);
        Write(text);
        return ResetStyle();
    }

    public AnsiWriter Write(string text, in Style style) => Write(text.AsSpan(), style);

    public AnsiWriter WriteLine(ReadOnlySpan<char> text = default)
    {
        Write(text);
        return Raw("\r\n"u8);
    }

    public AnsiWriter WriteLine(string? text) => WriteLine(text.AsSpan());

    public AnsiWriter Write(int value)
    {
        if (value < 0)
        {
            Ensure(1);
            _buffer[_pos++] = (byte)'-';
            WriteUInt((uint)(-(long)value));
        }
        else
        {
            WriteUInt((uint)value);
        }
        return this;
    }

    /// <summary>Writes bytes verbatim (already encoded text or sequences).</summary>
    public AnsiWriter Raw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_pos));
        _pos += bytes.Length;
        return this;
    }

    /// <summary>Writes <paramref name="count"/> copies of a character.</summary>
    public AnsiWriter Repeat(char c, int count)
    {
        if (count <= 0)
            return this;
        if (c < 0x80)
        {
            Ensure(count);
            _buffer.AsSpan(_pos, count).Fill((byte)c);
            _pos += count;
            return this;
        }
        for (int i = 0; i < count; i++)
            Write(c);
        return this;
    }

    private byte EncodeSingleByte(Rune r) => Encoding == TextEncoding.Cp437
        ? Cp437.Encode(r)
        : r.Value < 0x80 ? (byte)r.Value : (byte)'?';

    #endregion

    #region Cursor

    public AnsiWriter MoveTo(int x, int y)
    {
        Ensure(24);
        Csi();
        WriteUInt((uint)Math.Max(0, y) + 1);
        Put((byte)';');
        WriteUInt((uint)Math.Max(0, x) + 1);
        Put((byte)'H');
        return this;
    }

    public AnsiWriter Home() => Raw("\e[H"u8);

    public AnsiWriter MoveUp(int n = 1) => CsiN(n, 'A');
    public AnsiWriter MoveDown(int n = 1) => CsiN(n, 'B');
    public AnsiWriter MoveRight(int n = 1) => CsiN(n, 'C');
    public AnsiWriter MoveLeft(int n = 1) => CsiN(n, 'D');
    public AnsiWriter NextLine(int n = 1) => CsiN(n, 'E');
    public AnsiWriter PreviousLine(int n = 1) => CsiN(n, 'F');
    public AnsiWriter MoveToColumn(int x) => CsiParam(x + 1, 'G');
    public AnsiWriter MoveToRow(int y) => CsiParam(y + 1, 'd');

    /// <summary>DECSC; saves position and attributes.</summary>
    public AnsiWriter SaveCursor() => Raw("\e7"u8);
    public AnsiWriter RestoreCursor() => Raw("\e8"u8);

    public AnsiWriter ShowCursor() => Raw("\e[?25h"u8);
    public AnsiWriter HideCursor() => Raw("\e[?25l"u8);
    public AnsiWriter SetCursorVisible(bool visible) => visible ? ShowCursor() : HideCursor();

    public AnsiWriter SetCursorShape(CursorShape shape)
    {
        Ensure(8);
        Csi();
        WriteUInt((uint)shape);
        return Raw(" q"u8);
    }

    /// <summary>Asks for a cursor position report (<c>CSI row;col R</c>).</summary>
    public AnsiWriter RequestCursorPosition() => Raw("\e[6n"u8);

    #endregion

    #region Erase / edit / scroll

    public AnsiWriter ClearScreen() => Raw("\e[2J"u8);
    public AnsiWriter ClearScrollback() => Raw("\e[3J"u8);
    public AnsiWriter ClearToEndOfScreen() => Raw("\e[J"u8);
    public AnsiWriter ClearToStartOfScreen() => Raw("\e[1J"u8);
    public AnsiWriter ClearLine() => Raw("\e[2K"u8);
    public AnsiWriter ClearToEndOfLine() => Raw("\e[K"u8);
    public AnsiWriter ClearToStartOfLine() => Raw("\e[1K"u8);
    public AnsiWriter EraseCharacters(int n) => CsiN(n, 'X');

    public AnsiWriter InsertLines(int n = 1) => CsiN(n, 'L');
    public AnsiWriter DeleteLines(int n = 1) => CsiN(n, 'M');
    public AnsiWriter InsertCharacters(int n = 1) => CsiN(n, '@');
    public AnsiWriter DeleteCharacters(int n = 1) => CsiN(n, 'P');
    public AnsiWriter ScrollUp(int n = 1) => CsiN(n, 'S');
    public AnsiWriter ScrollDown(int n = 1) => CsiN(n, 'T');

    /// <summary>DECSTBM; rows are zero-based and inclusive.</summary>
    public AnsiWriter SetScrollRegion(int top, int bottom)
    {
        Ensure(24);
        Csi();
        WriteUInt((uint)top + 1);
        Put((byte)';');
        WriteUInt((uint)bottom + 1);
        Put((byte)'r');
        return this;
    }

    public AnsiWriter ResetScrollRegion() => Raw("\e[r"u8);

    #endregion

    #region Style

    public AnsiWriter ResetStyle() => Raw("\e[0m"u8);

    /// <summary>Resets, then applies the full style.</summary>
    public AnsiWriter SetStyle(in Style style)
    {
        Ensure(64);
        Csi();
        Put((byte)'0');
        AppendAttributesOn(style.Attributes);
        AppendColors(style, true, true);
        Put((byte)'m');
        return this;
    }

    /// <summary>Emits the shortest SGR that turns <paramref name="from"/> into <paramref name="to"/>.</summary>
    public AnsiWriter TransitionStyle(in Style from, in Style to)
    {
        var f = Downsampled(from);
        var t = Downsampled(to);
        if (f == t)
            return this;
        if (LegacyBrightColors || t == default)
            return t == default ? ResetStyle() : SetStyle(to);

        var removed = f.Attributes & ~t.Attributes;
        var added = t.Attributes & ~f.Attributes;

        // Bold and dim share an "off" code, so turning off one may need the other re-enabled.
        if ((removed & (TextAttributes.Bold | TextAttributes.Dim)) != 0)
            added |= t.Attributes & (TextAttributes.Bold | TextAttributes.Dim);
        if ((removed & (TextAttributes.Underline | TextAttributes.DoubleUnderline)) != 0)
            added |= t.Attributes & (TextAttributes.Underline | TextAttributes.DoubleUnderline);

        Ensure(80);
        Csi();
        int start = _pos;
        AppendAttributesOff(removed);
        AppendAttributesOn(added);
        AppendColors(t, f.Foreground != t.Foreground, f.Background != t.Background);
        return FinishSgr(start);
    }

    public AnsiWriter SetForeground(Color color)
    {
        Ensure(24);
        Csi();
        int start = _pos;
        AppendColor(Prepare(color), false);
        return FinishSgr(start);
    }

    public AnsiWriter SetBackground(Color color)
    {
        Ensure(24);
        Csi();
        int start = _pos;
        AppendColor(Prepare(color), true);
        return FinishSgr(start);
    }

    /// <summary>Turns on the given attributes without touching others.</summary>
    public AnsiWriter AddAttributes(TextAttributes attributes)
    {
        Ensure(48);
        Csi();
        int start = _pos;
        AppendAttributesOn(attributes);
        return FinishSgr(start);
    }

    public AnsiWriter RemoveAttributes(TextAttributes attributes)
    {
        Ensure(48);
        Csi();
        int start = _pos;
        AppendAttributesOff(attributes);
        return FinishSgr(start);
    }

    /// <summary>True when colors are written exactly as given, with no downsampling or rounding.</summary>
    internal bool ColorsExact => ColorMode == ColorMode.TrueColor && _precision == 8;

    /// <summary>The style as it will actually be written: downsampled to the color mode and rounded to the precision.</summary>
    internal Style Downsampled(in Style s) => ColorMode == ColorMode.TrueColor && _precision == 8
        ? s
        : new Style(Prepare(s.Foreground), Prepare(s.Background), s.Attributes);

    private Color Prepare(Color c)
    {
        c = c.Downsample(ColorMode);
        if (_precision == 8 || c.Kind != ColorKind.Rgb)
            return c;
        int max = (1 << _precision) - 1;
        return Color.Rgb(Quantize(c.R, max), Quantize(c.G, max), Quantize(c.B, max));
    }

    // Rounds to the nearest of max+1 evenly spaced levels across 0..255; applying it twice changes nothing.
    private static byte Quantize(byte v, int max) => (byte)(((v * max + 127) / 255 * 255 + max / 2) / max);

    private AnsiWriter FinishSgr(int start)
    {
        if (_pos == start)
        {
            _pos -= 2; // nothing to say, drop the CSI
            return this;
        }
        if (_buffer[start] == (byte)';')
        {
            Buffer.BlockCopy(_buffer, start + 1, _buffer, start, _pos - start - 1);
            _pos--;
        }
        Put((byte)'m');
        return this;
    }

    private void AppendAttributesOn(TextAttributes a)
    {
        if (a == 0)
            return;
        if ((a & TextAttributes.Bold) != 0) Sgr(1);
        if ((a & TextAttributes.Dim) != 0) Sgr(2);
        if ((a & TextAttributes.Italic) != 0) Sgr(3);
        if ((a & TextAttributes.Underline) != 0) Sgr(4);
        if ((a & TextAttributes.Blink) != 0) Sgr(5);
        if ((a & TextAttributes.Inverse) != 0) Sgr(7);
        if ((a & TextAttributes.Hidden) != 0) Sgr(8);
        if ((a & TextAttributes.Strikethrough) != 0) Sgr(9);
        if ((a & TextAttributes.DoubleUnderline) != 0) Sgr(21);
        if ((a & TextAttributes.Overline) != 0) Sgr(53);
    }

    private void AppendAttributesOff(TextAttributes a)
    {
        if (a == 0)
            return;
        if ((a & (TextAttributes.Bold | TextAttributes.Dim)) != 0) Sgr(22);
        if ((a & TextAttributes.Italic) != 0) Sgr(23);
        if ((a & (TextAttributes.Underline | TextAttributes.DoubleUnderline)) != 0) Sgr(24);
        if ((a & TextAttributes.Blink) != 0) Sgr(25);
        if ((a & TextAttributes.Inverse) != 0) Sgr(27);
        if ((a & TextAttributes.Hidden) != 0) Sgr(28);
        if ((a & TextAttributes.Strikethrough) != 0) Sgr(29);
        if ((a & TextAttributes.Overline) != 0) Sgr(55);
    }

    private void AppendColors(in Style s, bool fg, bool bg)
    {
        if (ColorMode == ColorMode.None)
            return;
        if (fg)
            AppendColor(Prepare(s.Foreground), false);
        if (bg)
            AppendColor(Prepare(s.Background), true);
    }

    private void AppendColor(Color c, bool background)
    {
        if (ColorMode == ColorMode.None)
            return;
        switch (c.Kind)
        {
            case ColorKind.Default:
                Sgr(background ? 49 : 39);
                break;
            case ColorKind.Indexed when c.Index < 8:
                Sgr((background ? 40 : 30) + c.Index);
                break;
            case ColorKind.Indexed when c.Index < 16:
                if (LegacyBrightColors)
                {
                    Sgr(background ? 5 : 1);
                    Sgr((background ? 40 : 30) + c.Index - 8);
                }
                else
                {
                    Sgr((background ? 100 : 90) + c.Index - 8);
                }
                break;
            case ColorKind.Indexed:
                Sgr(background ? 48 : 38);
                Sgr(5);
                Sgr(c.Index);
                break;
            default:
                Sgr(background ? 48 : 38);
                Sgr(2);
                Sgr(c.R);
                Sgr(c.G);
                Sgr(c.B);
                break;
        }
    }

    private void Sgr(int n)
    {
        Put((byte)';');
        WriteUInt((uint)n);
    }

    #endregion

    #region Modes

    public AnsiWriter SetMode(PrivateMode mode, bool enabled)
    {
        Ensure(16);
        Raw("\e[?"u8);
        WriteUInt((uint)mode);
        Put(enabled ? (byte)'h' : (byte)'l');
        return this;
    }

    public AnsiWriter EnterAlternateScreen() => Raw("\e[?1049h"u8);
    public AnsiWriter LeaveAlternateScreen() => Raw("\e[?1049l"u8);
    public AnsiWriter BeginSynchronizedUpdate() => Raw("\e[?2026h"u8);
    public AnsiWriter EndSynchronizedUpdate() => Raw("\e[?2026l"u8);

    /// <summary>Kitty progressive keyboard enhancement; flags 1 = disambiguate escape codes.</summary>
    public AnsiWriter PushKeyboardFlags(int flags = 1) => CsiPrefixed('>', flags, 'u');
    public AnsiWriter PopKeyboardFlags() => Raw("\e[<u"u8);

    /// <summary>DECSTR; resets modes and attributes without clearing the screen.</summary>
    public AnsiWriter SoftReset() => Raw("\e[!p"u8);

    /// <summary>RIS; resets everything, including the screen.</summary>
    public AnsiWriter FullReset() => Raw("\ec"u8);

    public AnsiWriter Bell() => Raw("\a"u8);

    #endregion

    #region OSC

    public AnsiWriter SetTitle(ReadOnlySpan<char> title)
    {
        Raw("\e]0;"u8);
        WriteOscText(title);
        return Raw("\e\\"u8);
    }

    /// <summary>OSC 8 hyperlink around <paramref name="text"/>.</summary>
    public AnsiWriter Hyperlink(ReadOnlySpan<char> uri, ReadOnlySpan<char> text)
    {
        BeginHyperlink(uri);
        Write(text);
        return EndHyperlink();
    }

    public AnsiWriter BeginHyperlink(ReadOnlySpan<char> uri)
    {
        Raw("\e]8;;"u8);
        WriteOscText(uri);
        return Raw("\e\\"u8);
    }

    public AnsiWriter EndHyperlink() => Raw("\e]8;;\e\\"u8);

    /// <summary>OSC 52 clipboard write. Many terminals require this to be enabled by the user.</summary>
    public AnsiWriter SetClipboard(ReadOnlySpan<char> text)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text.ToArray());
        Raw("\e]52;c;"u8);
        int len = Base64.GetMaxEncodedToUtf8Length(utf8.Length);
        Ensure(len);
        Base64.EncodeToUtf8(utf8, _buffer.AsSpan(_pos), out _, out int written);
        _pos += written;
        return Raw("\e\\"u8);
    }

    private void WriteOscText(ReadOnlySpan<char> text)
    {
        // Control characters would terminate or corrupt the sequence.
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Value >= 0x20 && r.Value != 0x7F && (r.Value < 0x80 || r.Value >= 0xA0))
                Write(r);
        }
    }

    #endregion

    #region Primitives

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int extra)
    {
        if (_buffer.Length - _pos < extra)
            Grow(extra);
    }

    private void Grow(int extra)
    {
        int size = Math.Max(_buffer.Length * 2, _pos + extra);
        Array.Resize(ref _buffer, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        Ensure(1);
        _buffer[_pos++] = b;
    }

    private void Csi()
    {
        Ensure(2);
        _buffer[_pos++] = 0x1B;
        _buffer[_pos++] = (byte)'[';
    }

    private AnsiWriter CsiN(int n, char final)
    {
        if (n <= 0)
            return this;
        Ensure(16);
        Csi();
        if (n != 1)
            WriteUInt((uint)n);
        Put((byte)final);
        return this;
    }

    private AnsiWriter CsiParam(int n, char final)
    {
        Ensure(16);
        Csi();
        WriteUInt((uint)Math.Max(1, n));
        Put((byte)final);
        return this;
    }

    private AnsiWriter CsiPrefixed(char prefix, int n, char final)
    {
        Ensure(16);
        Csi();
        Put((byte)prefix);
        WriteUInt((uint)n);
        Put((byte)final);
        return this;
    }

    private void WriteUInt(uint v)
    {
        Ensure(10);
        var buf = _buffer;
        if (v < 10)
        {
            buf[_pos++] = (byte)('0' + v);
            return;
        }
        if (v < 100)
        {
            buf[_pos++] = (byte)('0' + v / 10);
            buf[_pos++] = (byte)('0' + v % 10);
            return;
        }
        if (v < 1000)
        {
            buf[_pos++] = (byte)('0' + v / 100);
            buf[_pos++] = (byte)('0' + v / 10 % 10);
            buf[_pos++] = (byte)('0' + v % 10);
            return;
        }
        int digits = 4;
        for (uint t = v / 10000; t != 0; t /= 10)
            digits++;
        int end = _pos + digits;
        for (int i = end - 1; i >= _pos; i--)
        {
            buf[i] = (byte)('0' + v % 10);
            v /= 10;
        }
        _pos = end;
    }

    #endregion

    private sealed class StreamSink(Stream stream) : IOutputSink
    {
        public void Write(ReadOnlySpan<byte> data)
        {
            stream.Write(data);
            stream.Flush();
        }
    }
}
