using System;
using System.Collections.Generic;
using System.Text;

namespace AnsiDotNet.Layout;

public enum WrapMode : byte
{
    /// <summary>Lines break only at newlines.</summary>
    None,
    /// <summary>Breaks anywhere.</summary>
    Character,
    /// <summary>Breaks at spaces, after hyphens and between wide (CJK) characters; splits words only when one doesn't fit on a line by itself.</summary>
    Word,
}

/// <summary>
/// One line of laid-out text: a slice of the source (<see cref="Start"/>, <see cref="Length"/> in chars) and its
/// width in cells. Trailing spaces are not included. <see cref="Wrapped"/> is true when the line was broken to fit,
/// rather than ending at a newline or the end of the text.
/// </summary>
public readonly record struct TextLine(int Start, int Length, int Width, bool Wrapped);

/// <summary>Lazily breaks text into lines without allocating. Use with <c>foreach</c>.</summary>
public ref struct LineBreaker
{
    private readonly ReadOnlySpan<char> _text;
    private readonly int _width, _tab;
    private readonly WrapMode _mode;
    private int _pos;
    private bool _emptyTail, _done;

    public LineBreaker(ReadOnlySpan<char> text, int width, WrapMode mode = WrapMode.Word, int tabSize = 4)
    {
        _text = text;
        _width = width;
        _mode = mode;
        _tab = Math.Max(1, tabSize);
        Current = default;
    }

    public TextLine Current { get; private set; }

    public readonly LineBreaker GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_done)
            return false;
        int len = _text.Length;
        if (_pos >= len)
        {
            _done = true;
            if (!_emptyTail)
                return false;
            Current = new TextLine(len, 0, 0, false);
            return true;
        }

        bool wrap = _mode != WrapMode.None && _width > 0;
        int start = _pos, i = start, col = 0;
        int contentEnd = start, contentWidth = 0;
        int breakEnd = start, breakWidth = 0, breakNext = -1;
        bool prevSpace = false;

        while (i < len)
        {
            char c = _text[i];
            if (c == '\n')
                return Emit(start, contentEnd, contentWidth, false, i + 1);

            int n = 1, w;
            if (c == '\t')
                w = _tab - col % _tab;
            else if (char.IsHighSurrogate(c) && i + 1 < len && char.IsLowSurrogate(_text[i + 1]))
            {
                w = CellWidth.Of(new Rune(c, _text[i + 1]));
                n = 2;
            }
            else
                w = CellWidth.Of(c);
            bool space = c is ' ' or '\t';

            if (wrap && w > 0 && col + w > _width)
            {
                if (space)
                {
                    int next = i;
                    while (next < len && _text[next] is ' ' or '\t')
                        next++;
                    // A newline right after the break would otherwise produce an extra empty line.
                    bool hard = next < len && _text[next] == '\n';
                    return Emit(start, contentEnd, contentWidth, !hard, hard ? next + 1 : next);
                }
                if (_mode == WrapMode.Word && breakEnd > start)
                    return Emit(start, breakEnd, breakWidth, true, breakNext);
                if (i > start)
                    return Emit(start, contentEnd, contentWidth, true, i);
                // A single glyph wider than the line: take it anyway so we always make progress.
            }

            if (space)
            {
                if (!prevSpace)
                {
                    breakEnd = contentEnd;
                    breakWidth = contentWidth;
                }
                breakNext = i + n;
            }
            col += w;
            i += n;
            prevSpace = space;
            if (!space)
            {
                contentEnd = i;
                contentWidth = col;
                if (w == 2 || (c == '-' && i - start > 1))
                {
                    breakEnd = i;
                    breakWidth = col;
                    breakNext = i;
                }
            }
        }
        return Emit(start, contentEnd, contentWidth, false, len);
    }

    private bool Emit(int start, int end, int width, bool wrapped, int next)
    {
        Current = new TextLine(start, end - start, width, wrapped);
        _emptyTail = !wrapped && next == _text.Length && next > 0 && _text[next - 1] == '\n';
        _pos = next;
        return true;
    }
}

public static class TextLayout
{
    public static LineBreaker Lines(ReadOnlySpan<char> text, int width, WrapMode mode = WrapMode.Word, int tabSize = 4) =>
        new(text, width, mode, tabSize);

    /// <summary>The size the text needs when wrapped to <paramref name="maxWidth"/>.</summary>
    public static TerminalSize Measure(ReadOnlySpan<char> text, int maxWidth = int.MaxValue, WrapMode mode = WrapMode.Word, int tabSize = 4)
    {
        int w = 0, h = 0;
        foreach (var line in new LineBreaker(text, maxWidth, mode, tabSize))
        {
            w = Math.Max(w, line.Width);
            h++;
        }
        return new TerminalSize(w, h);
    }

    /// <summary>Replaces the contents of <paramref name="lines"/> with the wrapped lines. Returns the widest line's width.</summary>
    public static int Wrap(ReadOnlySpan<char> text, int width, List<TextLine> lines, WrapMode mode = WrapMode.Word, int tabSize = 4)
    {
        lines.Clear();
        int w = 0;
        foreach (var line in new LineBreaker(text, width, mode, tabSize))
        {
            lines.Add(line);
            w = Math.Max(w, line.Width);
        }
        return w;
    }
}
