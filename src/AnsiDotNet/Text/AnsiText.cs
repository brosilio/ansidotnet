using System;
using System.Text;

namespace AnsiDotNet;

public enum AnsiTokenKind : byte
{
    Text,
    /// <summary>A single C0 control character (CR, LF, BEL, ...).</summary>
    Control,
    /// <summary><c>ESC [ ... final</c></summary>
    Csi,
    /// <summary>OSC, DCS, APC, PM and SOS strings.</summary>
    String,
    /// <summary>Any other escape (<c>ESC 7</c>, <c>ESC ( B</c>, ...) or a truncated sequence.</summary>
    Escape,
}

public readonly ref struct AnsiToken
{
    public AnsiTokenKind Kind { get; init; }
    /// <summary>The complete token as it appeared in the source.</summary>
    public ReadOnlySpan<char> Raw { get; init; }
    /// <summary>For CSI: '?', '&lt;', '=', '&gt;' or '\0'.</summary>
    public char Prefix { get; init; }
    /// <summary>For CSI: the parameter bytes, without prefix or final.</summary>
    public ReadOnlySpan<char> Parameters { get; init; }
    /// <summary>For CSI and escapes: the final character.</summary>
    public char Final { get; init; }

    /// <summary>Parses CSI parameters (sub-parameters after ':' are skipped). Missing values are 0.</summary>
    public int GetParameters(Span<int> destination) => AnsiText.ParseParameters(Parameters, destination);
}

/// <summary>Splits text containing escape sequences into tokens. Allocation-free.</summary>
public ref struct AnsiTokenizer
{
    private readonly ReadOnlySpan<char> _text;
    private int _pos;

    public AnsiTokenizer(ReadOnlySpan<char> text)
    {
        _text = text;
        _pos = 0;
        Current = default;
    }

    public AnsiToken Current { get; private set; }

    public readonly AnsiTokenizer GetEnumerator() => this;

    public bool MoveNext()
    {
        var t = _text;
        int start = _pos;
        if (start >= t.Length)
            return false;

        char c = t[start];
        if (c == '\e')
            return ReadEscape(start);

        if (c < 0x20 || c == 0x7F)
        {
            _pos = start + 1;
            Current = new AnsiToken { Kind = AnsiTokenKind.Control, Raw = t.Slice(start, 1), Final = c };
            return true;
        }

        int i = start + 1;
        while (i < t.Length && t[i] >= 0x20 && t[i] != 0x7F && t[i] != '\e')
            i++;
        _pos = i;
        Current = new AnsiToken { Kind = AnsiTokenKind.Text, Raw = t[start..i] };
        return true;
    }

    private bool ReadEscape(int start)
    {
        var t = _text;
        int i = start + 1;
        if (i >= t.Length)
            return Finish(AnsiTokenKind.Escape, start, t.Length, '\0');

        char n = t[i];
        if (n == '[')
        {
            i++;
            char prefix = '\0';
            if (i < t.Length && t[i] is '?' or '<' or '=' or '>')
                prefix = t[i++];
            int ps = i;
            while (i < t.Length && t[i] >= 0x30 && t[i] <= 0x3F)
                i++;
            int pe = i;
            while (i < t.Length && t[i] >= 0x20 && t[i] <= 0x2F)
                i++;
            if (i < t.Length && t[i] >= 0x40 && t[i] <= 0x7E)
            {
                _pos = i + 1;
                Current = new AnsiToken
                {
                    Kind = AnsiTokenKind.Csi,
                    Raw = t[start.._pos],
                    Prefix = prefix,
                    Parameters = t[ps..pe],
                    Final = t[i],
                };
                return true;
            }
            return Finish(AnsiTokenKind.Escape, start, i, '\0');
        }

        if (n is ']' or 'P' or '_' or '^' or 'X')
        {
            for (i++; i < t.Length; i++)
            {
                if (t[i] == '\a')
                    return Finish(AnsiTokenKind.String, start, i + 1, n);
                if (t[i] == '\e' && i + 1 < t.Length && t[i + 1] == '\\')
                    return Finish(AnsiTokenKind.String, start, i + 2, n);
            }
            return Finish(AnsiTokenKind.Escape, start, t.Length, '\0');
        }

        while (i < t.Length && t[i] >= 0x20 && t[i] <= 0x2F)
            i++;
        if (i < t.Length)
            return Finish(AnsiTokenKind.Escape, start, i + 1, t[i]);
        return Finish(AnsiTokenKind.Escape, start, t.Length, '\0');
    }

    private bool Finish(AnsiTokenKind kind, int start, int end, char final)
    {
        _pos = end;
        Current = new AnsiToken { Kind = kind, Raw = _text[start..end], Final = final };
        return true;
    }
}

public static class AnsiText
{
    /// <summary>Removes escape sequences and control characters (except CR, LF and TAB).</summary>
    public static string Strip(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var tok in new AnsiTokenizer(text))
        {
            if (tok.Kind == AnsiTokenKind.Text)
                sb.Append(tok.Raw);
            else if (tok.Kind == AnsiTokenKind.Control && tok.Final is '\r' or '\n' or '\t')
                sb.Append(tok.Final);
        }
        return sb.ToString();
    }

    /// <summary>Column width of the visible text of a single line, ignoring escape sequences.</summary>
    public static int VisibleWidth(ReadOnlySpan<char> text)
    {
        int w = 0;
        foreach (var tok in new AnsiTokenizer(text))
        {
            if (tok.Kind == AnsiTokenKind.Text)
                w += CellWidth.Of(tok.Raw);
        }
        return w;
    }

    public static int ParseParameters(ReadOnlySpan<char> s, Span<int> destination)
    {
        if (s.IsEmpty || destination.IsEmpty)
            return 0;
        int count = 0, cur = 0;
        bool sub = false;
        foreach (char c in s)
        {
            if (c >= '0' && c <= '9')
            {
                if (!sub && cur < 100_000)
                    cur = cur * 10 + (c - '0');
            }
            else if (c == ';')
            {
                destination[count++] = cur;
                if (count == destination.Length)
                    return count;
                cur = 0;
                sub = false;
            }
            else if (c == ':')
            {
                sub = true;
            }
        }
        destination[count++] = cur;
        return count;
    }

    /// <summary>Applies SGR parameters to a style, as a terminal would.</summary>
    public static Style ApplySgr(Style style, ReadOnlySpan<int> p)
    {
        if (p.IsEmpty)
            return default;
        var fg = style.Foreground;
        var bg = style.Background;
        var a = style.Attributes;
        for (int i = 0; i < p.Length; i++)
        {
            int v = p[i];
            switch (v)
            {
                case 0: fg = default; bg = default; a = 0; break;
                case 1: a |= TextAttributes.Bold; break;
                case 2: a |= TextAttributes.Dim; break;
                case 3: a |= TextAttributes.Italic; break;
                case 4: a |= TextAttributes.Underline; break;
                case 5:
                case 6: a |= TextAttributes.Blink; break;
                case 7: a |= TextAttributes.Inverse; break;
                case 8: a |= TextAttributes.Hidden; break;
                case 9: a |= TextAttributes.Strikethrough; break;
                case 21: a |= TextAttributes.DoubleUnderline; break;
                case 22: a &= ~(TextAttributes.Bold | TextAttributes.Dim); break;
                case 23: a &= ~TextAttributes.Italic; break;
                case 24: a &= ~(TextAttributes.Underline | TextAttributes.DoubleUnderline); break;
                case 25: a &= ~TextAttributes.Blink; break;
                case 27: a &= ~TextAttributes.Inverse; break;
                case 28: a &= ~TextAttributes.Hidden; break;
                case 29: a &= ~TextAttributes.Strikethrough; break;
                case 39: fg = default; break;
                case 49: bg = default; break;
                case 53: a |= TextAttributes.Overline; break;
                case 55: a &= ~TextAttributes.Overline; break;
                case >= 30 and <= 37: fg = Color.Indexed((byte)(v - 30)); break;
                case >= 40 and <= 47: bg = Color.Indexed((byte)(v - 40)); break;
                case >= 90 and <= 97: fg = Color.Indexed((byte)(v - 90 + 8)); break;
                case >= 100 and <= 107: bg = Color.Indexed((byte)(v - 100 + 8)); break;
                case 38:
                case 48:
                    if (i + 2 < p.Length && p[i + 1] == 5)
                    {
                        var c = Color.Indexed((byte)p[i + 2]);
                        if (v == 38) fg = c; else bg = c;
                        i += 2;
                    }
                    else if (i + 4 < p.Length && p[i + 1] == 2)
                    {
                        var c = Color.Rgb((byte)p[i + 2], (byte)p[i + 3], (byte)p[i + 4]);
                        if (v == 38) fg = c; else bg = c;
                        i += 4;
                    }
                    else
                    {
                        i = p.Length;
                    }
                    break;
            }
        }
        return new Style(fg, bg, a);
    }
}
