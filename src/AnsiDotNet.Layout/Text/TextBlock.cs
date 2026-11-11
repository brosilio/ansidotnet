using System;
using System.Collections.Generic;
using System.Text;

namespace AnsiDotNet.Layout;

/// <summary>
/// A paragraph of plain or rich text that wraps, aligns, justifies, scrolls and truncates within a rectangle.
/// Wrapped lines are cached per width, so redrawing an unchanged block every frame is cheap and allocation-free.
/// </summary>
public sealed class TextBlock
{
    private string _text = "";
    private RichText? _rich;
    private int _version;
    private readonly LineCache _cacheA = new(), _cacheB = new();
    private bool _useA;

    public TextBlock() { }

    public TextBlock(string text, Style style = default)
    {
        _text = text ?? "";
        Style = style;
    }

    public TextBlock(RichText text, Style style = default)
    {
        _rich = text;
        Style = style;
    }

    /// <summary>Plain text. Setting it detaches any <see cref="Rich"/> text.</summary>
    public string Text
    {
        get => _rich?.ToString() ?? _text;
        set
        {
            value ??= "";
            if (_rich is null && ReferenceEquals(value, _text))
                return;
            _text = value;
            _rich = null;
            _version++;
        }
    }

    /// <summary>Styled text; edits to it are picked up automatically.</summary>
    public RichText? Rich
    {
        get => _rich;
        set
        {
            _rich = value;
            _version++;
        }
    }

    /// <summary>Base style. Rich runs with default colors inherit these.</summary>
    public Style Style { get; set; }
    public TextAlign Align { get; set; }
    public Align VerticalAlign { get; set; }
    public WrapMode Wrap { get; set; } = WrapMode.Word;

    /// <summary>End truncated lines, and the last visible line when more text is hidden below, with "…".</summary>
    public bool Ellipsis { get; set; }

    public int TabSize { get; set; } = 4;

    /// <summary>Index of the first visible line.</summary>
    public int ScrollY { get; set; }

    /// <summary>Only this many chars of the text are drawn (for typewriter effects); the layout doesn't change.</summary>
    public int VisibleCharacters { get; set; } = int.MaxValue;

    public ReadOnlySpan<char> Span => _rich is null ? _text : _rich.Span;
    public int Length => _rich?.Length ?? _text.Length;

    /// <summary>The lines this text breaks into at <paramref name="width"/>. Valid until the text or settings change.</summary>
    public IReadOnlyList<TextLine> GetLines(int width) => Lines(width);

    public TerminalSize Measure(int maxWidth = int.MaxValue)
    {
        var cache = Get(maxWidth);
        return new TerminalSize(cache.MaxWidth, cache.Lines.Count);
    }

    public int LineCount(int width) => Lines(width).Count;

    private List<TextLine> Lines(int width) => Get(width).Lines;

    private LineCache Get(int width)
    {
        if (Wrap == WrapMode.None)
            width = int.MaxValue;
        object source = (object?)_rich ?? _text;
        long version = ((long)_version << 32) | (uint)(_rich?.Version ?? 0);
        if (_cacheA.Matches(source, version, width, Wrap, TabSize))
            return _cacheA;
        if (_cacheB.Matches(source, version, width, Wrap, TabSize))
            return _cacheB;
        // Two slots: measuring at one width and drawing at another is common, and shouldn't thrash.
        var c = _useA ? _cacheA : _cacheB;
        _useA = !_useA;
        c.Fill(Span, source, version, width, Wrap, TabSize);
        return c;
    }

    public void Draw(Surface surface, Rect area)
    {
        if (area.IsEmpty)
            return;
        var lines = Lines(area.Width);
        int total = lines.Count;
        int first = Math.Clamp(ScrollY, 0, Math.Max(0, total - 1));
        int count = Math.Min(area.Height, total - first);
        if (count <= 0)
            return;
        int y = area.Y + RectExtensions.Offset(area.Height - count, VerticalAlign);
        bool hiddenBelow = first + count < total;
        var text = Span;
        int run = -1;

        for (int r = 0; r < count; r++, y++)
        {
            var line = lines[first + r];
            if (line.Start >= VisibleCharacters)
                break;
            bool cut = line.Width > area.Width;
            bool ellipsis = Ellipsis && (cut || (hiddenBelow && r == count - 1));
            int limit = ellipsis ? area.Width - 1 : area.Width;
            int shown = Math.Min(line.Width, limit);

            int x = area.X;
            int gaps = 0, extra = 0;
            switch (Align)
            {
                case TextAlign.Center: x += (area.Width - shown) / 2; break;
                case TextAlign.Right: x += area.Width - shown; break;
                case TextAlign.Justify when line.Wrapped && !ellipsis && line.Width < area.Width:
                    gaps = CountGaps(text.Slice(line.Start, line.Length));
                    extra = area.Width - line.Width;
                    break;
            }

            int end = DrawLine(surface, x, y, text, line, limit, gaps, extra, ref run);
            if (ellipsis && line.Start + line.Length <= VisibleCharacters)
                surface.SetCell(Math.Min(end, area.X + area.Width - 1), y, '…', Resolve(text, line.Start + line.Length - 1, ref run));
        }
    }

    // Draws one line; returns the column after the last cell drawn.
    private int DrawLine(Surface s, int x0, int y, ReadOnlySpan<char> text, TextLine line, int limit, int gaps, int extra, ref int run)
    {
        int col = 0, drawn = 0, gap = 0;
        int end = Math.Min(line.Start + line.Length, VisibleCharacters);
        bool prevSpace = false;
        for (int i = line.Start; i < end;)
        {
            char c = text[i];
            int n = 1;
            Rune rune;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                rune = new Rune(c, text[i + 1]);
                n = 2;
            }
            else
                rune = Rune.TryCreate(c, out var rr) ? rr : Rune.ReplacementChar;

            var style = Resolve(text, i, ref run);
            if (c == '\t')
            {
                int w = TabSize - col % TabSize;
                if (drawn + w > limit)
                    break;
                for (int k = 0; k < w; k++)
                    s.SetCell(x0 + drawn + k, y, ' ', style);
                col += w;
                drawn += w;
            }
            else
            {
                int w = CellWidth.Of(rune);
                if (w > 0)
                {
                    if (drawn + w > limit)
                        break;
                    s.SetCell(x0 + drawn, y, rune, style);
                    col += w;
                    drawn += w;
                }
            }

            bool space = c is ' ' or '\t';
            if (space && !prevSpace && gaps > 0 && i > line.Start)
            {
                // Spread the spare width over the gaps between words, left gaps first.
                int add = extra / gaps + (gap < extra % gaps ? 1 : 0);
                gap++;
                for (int k = 0; k < add && drawn < limit; k++)
                    s.SetCell(x0 + drawn++, y, ' ', style);
            }
            prevSpace = space;
            i += n;
        }
        return x0 + drawn;
    }

    private Style Resolve(ReadOnlySpan<char> text, int index, ref int run)
    {
        if (_rich is null)
            return Style;
        var runs = _rich.Runs;
        if (run < 0 || run >= runs.Length || runs[run].Start > index)
            run = _rich.RunIndexAt(index);
        else
            while (run + 1 < runs.Length && runs[run + 1].Start <= index)
                run++;
        if (run < 0)
            return Style;
        var st = runs[run].Style;
        return new Style(
            st.Foreground.IsDefault ? Style.Foreground : st.Foreground,
            st.Background.IsDefault ? Style.Background : st.Background,
            st.Attributes | Style.Attributes);
    }

    private static int CountGaps(ReadOnlySpan<char> s)
    {
        int gaps = 0;
        bool prev = false;
        for (int i = 0; i < s.Length; i++)
        {
            bool space = s[i] is ' ' or '\t';
            if (space && !prev && i > 0)
                gaps++;
            prev = space;
        }
        return gaps;
    }

    private sealed class LineCache
    {
        public readonly List<TextLine> Lines = new();
        public int MaxWidth;
        private object? _source;
        private long _version = -1;
        private int _width = -1, _tab;
        private WrapMode _mode;

        public bool Matches(object source, long version, int width, WrapMode mode, int tab) =>
            ReferenceEquals(source, _source) && version == _version && width == _width && mode == _mode && tab == _tab;

        public void Fill(ReadOnlySpan<char> text, object source, long version, int width, WrapMode mode, int tab)
        {
            MaxWidth = TextLayout.Wrap(text, width, Lines, mode, tab);
            _source = source;
            _version = version;
            _width = width;
            _mode = mode;
            _tab = tab;
        }
    }
}
