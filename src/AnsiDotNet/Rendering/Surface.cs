using System;
using System.Text;

namespace AnsiDotNet;

public enum BoxStyle : byte
{
    Single,
    Double,
    Rounded,
    Heavy,
    Ascii,
}

public enum TextAlign : byte
{
    Left,
    Center,
    Right,
    /// <summary>Stretches wrapped lines to the full width by widening the gaps between words. Single lines stay left-aligned.</summary>
    Justify,
}

/// <summary>
/// A grid of styled cells that you draw into and hand to <see cref="ScreenRenderer"/>. Wide characters occupy two
/// cells; all drawing is clipped to <see cref="Clip"/>. Zero-width characters (combining marks) are dropped.
/// </summary>
public sealed class Surface
{
    private Cell[] _cells;
    private Rect _clip;

    public Surface(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _cells = new Cell[Width * Height];
        _clip = Bounds;
        Clear();
    }

    public Surface(TerminalSize size) : this(size.Width, size.Height) { }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public TerminalSize Size => new(Width, Height);
    public Rect Bounds => new(0, 0, Width, Height);

    /// <summary>Drawing outside this rectangle is discarded. Always kept within <see cref="Bounds"/>.</summary>
    public Rect Clip
    {
        get => _clip;
        set => _clip = value.Intersect(Bounds);
    }

    public void ResetClip() => _clip = Bounds;

    public Cell this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException(x >= Width || x < 0 ? nameof(x) : nameof(y));
            return _cells[y * Width + x];
        }
        set => SetCell(x, y, value.Rune, value.Style);
    }

    /// <summary>Direct access to a row. Writing here bypasses clipping and wide-character bookkeeping.</summary>
    public Span<Cell> GetRow(int y) => _cells.AsSpan(y * Width, Width);

    internal ReadOnlySpan<Cell> Cells => _cells.AsSpan(0, Width * Height);

    public void Resize(int width, int height, bool preserve = false)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (width == Width && height == Height)
            return;
        var old = _cells;
        int ow = Width, oh = Height;
        // Shrinking (or growing back) reuses the buffer, so off-screen surfaces that change size every frame don't allocate.
        if (preserve || _cells.Length < width * height)
            _cells = new Cell[width * height];
        Width = width;
        Height = height;
        _clip = Bounds;
        Clear();
        if (preserve)
        {
            int cw = Math.Min(ow, width);
            for (int y = 0; y < Math.Min(oh, height); y++)
                old.AsSpan(y * ow, cw).CopyTo(_cells.AsSpan(y * width, cw));
            for (int y = 0; y < Math.Min(oh, height); y++)
            {
                if (cw > 0 && cw < ow && old[y * ow + cw].IsContinuation)
                    _cells[y * width + cw - 1] = new Cell(' ', _cells[y * width + cw - 1].Style);
            }
        }
    }

    public void Resize(TerminalSize size, bool preserve = false) => Resize(size.Width, size.Height, preserve);

    /// <summary>Fills the clip region with spaces.</summary>
    public void Clear(Style style = default) => Fill(_clip, new Rune(' '), style);

    public void Fill(Rect area, Rune rune, Style style)
    {
        area = area.Intersect(_clip);
        if (area.IsEmpty)
            return;
        if (CellWidth.Of(rune) != 1)
            rune = new Rune(' ');
        var cell = new Cell(rune, style);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            BreakWide(area.X, y);
            BreakWide(area.Right - 1, y);
            _cells.AsSpan(y * Width + area.X, area.Width).Fill(cell);
        }
    }

    public void Fill(Rect area, char c, Style style) => Fill(area, new Rune(c), style);

    /// <summary>Changes the style of cells in an area, keeping their characters.</summary>
    public void SetStyle(Rect area, Style style)
    {
        area = area.Intersect(_clip);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            var row = GetRow(y);
            for (int x = area.X; x < area.Right; x++)
                row[x] = new Cell(row[x].Rune, style);
        }
    }

    public void SetCell(int x, int y, Rune rune, Style style)
    {
        if (!_clip.Contains(x, y))
            return;
        int w = CellWidth.Of(rune);
        if (w == 0)
            return;
        if (w == 2 && !_clip.Contains(x + 1, y))
        {
            rune = new Rune(' ');
            w = 1;
        }
        BreakWide(x, y);
        _cells[y * Width + x] = new Cell(rune, style);
        if (w == 2)
        {
            BreakWide(x + 1, y);
            _cells[y * Width + x + 1] = Cell.Continuation(style);
        }
    }

    public void SetCell(int x, int y, char c, Style style) =>
        SetCell(x, y, Rune.TryCreate(c, out var r) ? r : Rune.ReplacementChar, style);

    /// <summary>Prints a single line of text. Control characters are skipped. Returns the column after the text.</summary>
    public int Print(int x, int y, ReadOnlySpan<char> text, Style style = default)
    {
        if (y < _clip.Y || y >= _clip.Bottom)
            return x + CellWidth.Of(text);
        foreach (var r in text.EnumerateRunes())
        {
            int w = CellWidth.Of(r);
            if (w == 0)
                continue;
            SetCell(x, y, r, style);
            x += w;
        }
        return x;
    }

    public int Print(int x, int y, string? text, Style style = default) => Print(x, y, text.AsSpan(), style);

    /// <summary>Prints at most <paramref name="maxWidth"/> columns.</summary>
    public int Print(int x, int y, ReadOnlySpan<char> text, Style style, int maxWidth)
    {
        var saved = _clip;
        _clip = _clip.Intersect(new Rect(x, y, Math.Max(0, maxWidth), 1));
        try
        {
            return Print(x, y, text, style);
        }
        finally
        {
            _clip = saved;
        }
    }

    /// <summary>Prints text aligned within <paramref name="width"/> columns, truncating with an ellipsis if needed.</summary>
    public void PrintAligned(int x, int y, int width, ReadOnlySpan<char> text, Style style, TextAlign alignment = TextAlign.Left)
    {
        if (width <= 0)
            return;
        int tw = CellWidth.Of(text);
        if (tw > width)
        {
            int n = CellWidth.Fit(text, width - 1, out _);
            int end = Print(x, y, text[..n], style);
            Print(end, y, "…", style);
            for (int i = end + 1; i < x + width; i++)
                SetCell(i, y, ' ', style);
            return;
        }
        int pad = alignment switch
        {
            TextAlign.Center => (width - tw) / 2,
            TextAlign.Right => width - tw,
            _ => 0,
        };
        Print(x + pad, y, text, style);
    }

    public void DrawHorizontalLine(int x, int y, int length, Style style, BoxStyle box = BoxStyle.Single)
    {
        var g = Glyphs(box);
        for (int i = 0; i < length; i++)
            SetCell(x + i, y, g[4], style);
    }

    public void DrawVerticalLine(int x, int y, int length, Style style, BoxStyle box = BoxStyle.Single)
    {
        var g = Glyphs(box);
        for (int i = 0; i < length; i++)
            SetCell(x, y + i, g[5], style);
    }

    /// <summary>Draws a border on the edge of <paramref name="area"/>, with an optional title in the top edge.</summary>
    public void DrawBox(Rect area, Style style, BoxStyle box = BoxStyle.Single, string? title = null, Style? titleStyle = null)
    {
        if (area.Width < 2 || area.Height < 2)
            return;
        var g = Glyphs(box);
        int r = area.Right - 1, b = area.Bottom - 1;
        SetCell(area.X, area.Y, g[0], style);
        SetCell(r, area.Y, g[1], style);
        SetCell(area.X, b, g[2], style);
        SetCell(r, b, g[3], style);
        DrawHorizontalLine(area.X + 1, area.Y, area.Width - 2, style, box);
        DrawHorizontalLine(area.X + 1, b, area.Width - 2, style, box);
        DrawVerticalLine(area.X, area.Y + 1, area.Height - 2, style, box);
        DrawVerticalLine(r, area.Y + 1, area.Height - 2, style, box);
        if (!string.IsNullOrEmpty(title) && area.Width > 4)
        {
            var ts = titleStyle ?? style;
            int max = area.X + area.Width - 2;
            int end = Print(area.X + 2, area.Y, " ", ts, max - area.X - 2);
            end = Print(end, area.Y, title, ts, max - end);
            Print(end, area.Y, " ", ts, max - end);
        }
    }

    /// <summary>Copies cells from another surface. Wide characters cut by the edges become spaces.</summary>
    public void Blit(Surface source, Rect sourceArea, int x, int y)
    {
        sourceArea = sourceArea.Intersect(source.Bounds);
        var dest = new Rect(x, y, sourceArea.Width, sourceArea.Height).Intersect(_clip);
        if (dest.IsEmpty)
            return;
        int sx0 = sourceArea.X + (dest.X - x), sy0 = sourceArea.Y + (dest.Y - y);
        for (int row = 0; row < dest.Height; row++)
        {
            int dy = dest.Y + row;
            BreakWide(dest.X, dy);
            BreakWide(dest.Right - 1, dy);
            var src = source.GetRow(sy0 + row).Slice(sx0, dest.Width);
            var dst = GetRow(dy).Slice(dest.X, dest.Width);
            src.CopyTo(dst);
            if (dst[0].IsContinuation)
                dst[0] = new Cell(' ', dst[0].Style);
            int last = dest.Width - 1;
            if (sx0 + last + 1 < source.Width && source.GetRow(sy0 + row)[sx0 + last + 1].IsContinuation && !dst[last].IsContinuation)
                dst[last] = new Cell(' ', dst[last].Style);
        }
    }

    public void Blit(Surface source, int x, int y) => Blit(source, source.Bounds, x, y);

    /// <summary>
    /// Composites <paramref name="source"/> over this surface at <paramref name="opacity"/> (0..1). Colors are mixed in
    /// RGB. Where the glyphs differ, the old glyph fades into the mixed background and then the new one fades in out of
    /// it, so text never pops. Default colors stand in as <paramref name="defaultForeground"/>/<paramref name="defaultBackground"/>
    /// (light gray on black if not given), since the terminal's real defaults are unknown.
    /// </summary>
    public void Blend(Surface source, Rect sourceArea, int x, int y, float opacity, Color defaultForeground = default, Color defaultBackground = default)
    {
        if (opacity >= 1f)
        {
            Blit(source, sourceArea, x, y);
            return;
        }
        if (opacity <= 0f)
            return;
        var dfg = defaultForeground.IsDefault ? DefaultBlendForeground : defaultForeground;
        var dbg = defaultBackground.IsDefault ? Color.Black : defaultBackground;
        sourceArea = sourceArea.Intersect(source.Bounds);
        var dest = new Rect(x, y, sourceArea.Width, sourceArea.Height).Intersect(_clip);
        if (dest.IsEmpty)
            return;
        int sx0 = sourceArea.X + (dest.X - x), sy0 = sourceArea.Y + (dest.Y - y);
        for (int row = 0; row < dest.Height; row++)
        {
            var src = source.GetRow(sy0 + row).Slice(sx0, dest.Width);
            var dst = GetRow(dest.Y + row).Slice(dest.X, dest.Width);
            for (int i = 0; i < dst.Length; i++)
            {
                var s = src[i];
                var d = dst[i];
                if (s == d)
                    continue;
                var sbg = Or(s.Style.Background, dbg);
                var dbgc = Or(d.Style.Background, dbg);
                var bg = Color.Lerp(dbgc, sbg, opacity);
                bool sBlank = IsBlank(s), dBlank = IsBlank(d);
                Cell result;
                if (s.Rune == d.Rune && !sBlank)
                {
                    result = new Cell(s.Rune, new Style(Color.Lerp(Or(d.Style.Foreground, dfg), Or(s.Style.Foreground, dfg), opacity), bg, s.Style.Attributes));
                }
                else if (sBlank && dBlank)
                {
                    result = new Cell(s.Rune, new Style(s.Style.Foreground, bg, s.Style.Attributes));
                }
                else if (sBlank || (!dBlank && opacity < 0.5f))
                {
                    float t = sBlank ? opacity : opacity * 2;
                    result = new Cell(d.Rune, new Style(Color.Lerp(Or(d.Style.Foreground, dfg), bg, t), bg, d.Style.Attributes));
                }
                else
                {
                    float t = dBlank ? opacity : (opacity - 0.5f) * 2;
                    result = new Cell(s.Rune, new Style(Color.Lerp(bg, Or(s.Style.Foreground, dfg), t), bg, s.Style.Attributes));
                }
                dst[i] = result;
            }
            FixWide(dst, dest.X, dest.Y + row);
        }

        static Color Or(Color c, Color fallback) => c.IsDefault ? fallback : c;
        static bool IsBlank(Cell c) => c.Rune.Value == ' ';
    }

    /// <summary>Mixes the colors in an area toward <paramref name="color"/> by <paramref name="amount"/> (0..1), e.g. to dim what is behind a dialog.</summary>
    public void Fade(Rect area, Color color, float amount, Color defaultForeground = default, Color defaultBackground = default)
    {
        area = area.Intersect(_clip);
        if (area.IsEmpty || amount <= 0f)
            return;
        var dfg = defaultForeground.IsDefault ? DefaultBlendForeground : defaultForeground;
        var dbg = defaultBackground.IsDefault ? Color.Black : defaultBackground;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            var row = GetRow(y).Slice(area.X, area.Width);
            for (int i = 0; i < row.Length; i++)
            {
                var c = row[i];
                var st = c.Style;
                var fg = Color.Lerp(st.Foreground.IsDefault ? dfg : st.Foreground, color, amount);
                var bg = Color.Lerp(st.Background.IsDefault ? dbg : st.Background, color, amount);
                row[i] = new Cell(c.Rune, new Style(fg, bg, st.Attributes));
            }
        }
    }

    private static readonly Color DefaultBlendForeground = Color.Rgb(204, 204, 204);

    // After raw cell copies, make sure every wide glyph still has its continuation and vice versa.
    private void FixWide(Span<Cell> span, int x0, int y)
    {
        var row = GetRow(y);
        int from = Math.Max(0, x0 - 1), to = Math.Min(Width, x0 + span.Length + 1);
        for (int x = from; x < to; x++)
        {
            var c = row[x];
            if (c.IsContinuation)
            {
                if (x == 0 || CellWidth.Of(row[x - 1].Rune) != 2)
                    row[x] = new Cell(' ', c.Style);
            }
            else if (c.Rune.Value > 0x7F && CellWidth.Of(c.Rune) == 2 && (x + 1 >= Width || !row[x + 1].IsContinuation))
            {
                row[x] = new Cell(' ', c.Style);
            }
        }
    }

    /// <summary>
    /// Interprets text containing SGR colors and basic cursor movement (as in ANSI art or colored program output).
    /// <paramref name="wrapWidth"/> wraps lines like a terminal of that width would (0 = no wrap).
    /// With <paramref name="legacyColors"/>, bold brightens the foreground and blink brightens the background.
    /// SUB (0x1A, the SAUCE marker) ends interpretation. Returns the final cursor position.
    /// </summary>
    public (int X, int Y) PrintAnsi(int x, int y, ReadOnlySpan<char> text, Style style = default, int wrapWidth = 0, bool legacyColors = false)
    {
        int cx = x, cy = y, sx = x, sy = y;
        Span<int> p = stackalloc int[32];
        foreach (var tok in new AnsiTokenizer(text))
        {
            switch (tok.Kind)
            {
                case AnsiTokenKind.Text:
                    var eff = legacyColors ? Brighten(style) : style;
                    foreach (var r in tok.Raw.EnumerateRunes())
                    {
                        int w = CellWidth.Of(r);
                        if (w == 0)
                            continue;
                        if (wrapWidth > 0 && cx + w > x + wrapWidth)
                        {
                            cx = x;
                            cy++;
                        }
                        SetCell(cx, cy, r, eff);
                        cx += w;
                    }
                    break;
                case AnsiTokenKind.Control:
                    switch (tok.Final)
                    {
                        case '\r': cx = x; break;
                        case '\n': cx = x; cy++; break;
                        case '\t': cx = x + ((cx - x) / 8 + 1) * 8; break;
                        case '\b': cx = Math.Max(x, cx - 1); break;
                        case '\x1A': return (cx, cy);
                    }
                    break;
                case AnsiTokenKind.Csi when tok.Prefix == '\0':
                    int n = tok.GetParameters(p);
                    int a0 = n > 0 && p[0] > 0 ? p[0] : 1;
                    switch (tok.Final)
                    {
                        case 'm': style = AnsiText.ApplySgr(style, p[..n]); break;
                        case 'A': cy = Math.Max(y, cy - a0); break;
                        case 'B': cy += a0; break;
                        case 'C': cx += a0; break;
                        case 'D': cx = Math.Max(x, cx - a0); break;
                        case 'G': cx = x + a0 - 1; break;
                        case 'H':
                        case 'f':
                            cy = y + a0 - 1;
                            cx = x + (n > 1 && p[1] > 0 ? p[1] : 1) - 1;
                            break;
                        case 's': sx = cx; sy = cy; break;
                        case 'u': cx = sx; cy = sy; break;
                    }
                    break;
            }
        }
        return (cx, cy);
    }

    private static Style Brighten(Style s)
    {
        var fg = s.Foreground;
        var bg = s.Background;
        if (s.Has(TextAttributes.Bold))
            fg = fg.IsDefault ? Color.BrightWhite : fg.Kind == ColorKind.Indexed && fg.Index < 8 ? Color.Indexed((byte)(fg.Index + 8)) : fg;
        if (s.Has(TextAttributes.Blink) && bg.Kind == ColorKind.Indexed && bg.Index < 8)
            bg = Color.Indexed((byte)(bg.Index + 8));
        return new Style(fg, bg, s.Attributes & ~(TextAttributes.Bold | TextAttributes.Blink));
    }

    /// <summary>Plain text of one row, for tests and debugging.</summary>
    public string GetText(int y)
    {
        var sb = new StringBuilder(Width);
        foreach (var c in GetRow(y))
        {
            if (!c.IsContinuation)
                sb.Append(c.Rune.ToString());
        }
        return sb.ToString();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int y = 0; y < Height; y++)
            sb.Append(GetText(y)).Append('\n');
        return sb.ToString();
    }

    // When overwriting half of a wide character, blank the other half.
    private void BreakWide(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        int i = y * Width + x;
        if (_cells[i].IsContinuation)
        {
            if (x > 0)
                _cells[i - 1] = new Cell(' ', _cells[i - 1].Style);
        }
        else if (x + 1 < Width && _cells[i + 1].IsContinuation)
        {
            _cells[i + 1] = new Cell(' ', _cells[i + 1].Style);
        }
    }

    private static readonly char[][] BoxGlyphs =
    [
        ['┌', '┐', '└', '┘', '─', '│'],
        ['╔', '╗', '╚', '╝', '═', '║'],
        ['╭', '╮', '╰', '╯', '─', '│'],
        ['┏', '┓', '┗', '┛', '━', '┃'],
        ['+', '+', '+', '+', '-', '|'],
    ];

    private static char[] Glyphs(BoxStyle box) => BoxGlyphs[(int)box];
}
