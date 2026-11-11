using System.Text;

namespace AnsiDotNet.Tests;

public class RenderingTests
{
    [Fact]
    public void WideCharactersOccupyTwoCells()
    {
        var s = new Surface(6, 1);
        int end = s.Print(0, 0, "a日b");
        Assert.Equal(4, end);
        Assert.True(s[2, 0].IsContinuation);
        Assert.Equal("a日b  ", s.GetText(0));

        // Overwriting the right half blanks the left half.
        s.SetCell(2, 0, 'x', default);
        Assert.Equal("a xb  ", s.GetText(0));
    }

    [Fact]
    public void WideCharacterAtEdgeBecomesSpace()
    {
        var s = new Surface(3, 1);
        s.Print(2, 0, "日");
        Assert.Equal("   ", s.GetText(0));
    }

    [Fact]
    public void ClipRestrictsDrawing()
    {
        var s = new Surface(10, 3) { Clip = new Rect(2, 1, 3, 1) };
        s.Fill(s.Bounds, '#', default);
        s.ResetClip();
        Assert.Equal("          ", s.GetText(0));
        Assert.Equal("  ###     ", s.GetText(1));
    }

    [Fact]
    public void PrintAlignedTruncates()
    {
        var s = new Surface(8, 2);
        s.PrintAligned(0, 0, 8, "hello world", default);
        s.PrintAligned(0, 1, 8, "hi", default, TextAlign.Right);
        Assert.Equal("hello w…", s.GetText(0));
        Assert.Equal("      hi", s.GetText(1));
    }

    [Fact]
    public void BoxWithTitle()
    {
        var s = new Surface(10, 3);
        s.DrawBox(s.Bounds, default, BoxStyle.Single, "Hi");
        Assert.Equal("┌─ Hi ───┐", s.GetText(0));
        Assert.Equal("│        │", s.GetText(1));
        Assert.Equal("└────────┘", s.GetText(2));
    }

    [Fact]
    public void BlitCopiesAndSplitsWideAtEdges()
    {
        var src = new Surface(4, 1);
        src.Print(0, 0, "日本");
        var dst = new Surface(4, 1);
        dst.Blit(src, new Rect(1, 0, 3, 1), 0, 0);
        Assert.Equal(" 本 ", dst.GetText(0));
    }

    [Fact]
    public void PrintAnsiInterpretsSgrAndMovement()
    {
        var s = new Surface(10, 2);
        var end = s.PrintAnsi(0, 0, "\e[31mab\e[0m\e[2Cc\r\nd\e[1;34me");
        Assert.Equal(Color.Red, s[0, 0].Style.Foreground);
        Assert.Equal(Color.Default, s[4, 0].Style.Foreground);
        Assert.Equal("ab  c     ", s.GetText(0));
        Assert.Equal("de        ", s.GetText(1));
        Assert.True(s[1, 1].Style.Has(TextAttributes.Bold));
        Assert.Equal((2, 1), end);

        var legacy = new Surface(2, 1);
        legacy.PrintAnsi(0, 0, "\e[1;34mX", legacyColors: true);
        Assert.Equal(Color.BrightBlue, legacy[0, 0].Style.Foreground);
    }

    [Fact]
    public void TokenizerAndStrip()
    {
        Assert.Equal("red plain", AnsiText.Strip("\e[31mred\e[0m \e]0;title\a\e(Bplain"));
        Assert.Equal(4, AnsiText.VisibleWidth("\e[1m日本\e[0m"));

        var kinds = new List<AnsiTokenKind>();
        foreach (var t in new AnsiTokenizer("a\e[?25l\rb\e7"))
            kinds.Add(t.Kind);
        Assert.Equal([AnsiTokenKind.Text, AnsiTokenKind.Csi, AnsiTokenKind.Control, AnsiTokenKind.Text, AnsiTokenKind.Escape], kinds);
    }

    [Fact]
    public void CellWidths()
    {
        Assert.Equal(1, CellWidth.Of(new Rune('a')));
        Assert.Equal(2, CellWidth.Of(new Rune(0x65E5)));
        Assert.Equal(2, CellWidth.Of(new Rune(0x1F600)));
        Assert.Equal(0, CellWidth.Of(new Rune(0x0301)));
        Assert.Equal(0, CellWidth.Of(new Rune(0x200D)));
        Assert.Equal(1, CellWidth.Of(new Rune('─')));
    }

    private static (ScreenRenderer, AnsiWriter) NewRenderer()
    {
        var w = new AnsiWriter();
        return (new ScreenRenderer(w) { SynchronizedOutput = false }, w);
    }

    private static string Take(AnsiWriter w)
    {
        var s = Encoding.UTF8.GetString(w.WrittenSpan);
        w.Clear();
        return s;
    }

    [Fact]
    public void UnchangedFramesSendNothingEvenWithSyncAndCursor()
    {
        var w = new AnsiWriter();
        var r = new ScreenRenderer(w) { SynchronizedOutput = true, Cursor = (2, 1) };
        var s = new Surface(5, 2);
        s.Print(0, 0, "hello");
        r.Render(s, flush: false);
        Assert.Contains("\e[?25h", Take(w));

        r.Render(s, flush: false);
        Assert.Equal("", Take(w)); // no sync wrappers, no cursor hide/show
        Assert.Equal(0, r.LastFrameBytes);

        r.Cursor = (3, 1); // the cursor moving alone is a change
        r.Render(s, flush: false);
        Assert.EndsWith("\e[?25h\e[?2026l", Take(w));

        r.Cursor = null;
        r.Render(s, flush: false);
        Assert.Contains("\e[?25l", Take(w));
        r.Render(s, flush: false);
        Assert.Equal("", Take(w));
    }

    [Fact]
    public void FirstFrameIsFullThenOnlyDiffs()
    {
        var (r, w) = NewRenderer();
        var s = new Surface(5, 2);
        s.Print(0, 0, "hello");
        r.Render(s, flush: false);
        string first = Take(w);
        Assert.Contains("\e[2J", first);
        Assert.Contains("hello", first);

        r.Render(s, flush: false);
        Assert.Equal("", Take(w));
        Assert.Equal(0, r.LastFrameCellsChanged);

        s.SetCell(4, 1, 'X', default);
        r.Render(s, flush: false);
        Assert.Equal("\e[2;5HX", Take(w));
    }

    [Fact]
    public void SmallGapsAreReprintedInsteadOfMoved()
    {
        var (r, w) = NewRenderer();
        var s = new Surface(10, 1);
        s.Print(0, 0, "abcdefghij");
        r.Render(s, flush: false);
        Take(w);

        s.Print(0, 0, "Abcdefghi!");
        r.Render(s, flush: false);
        string diff = Take(w);
        Assert.Equal("\e[1;1HA\e[8C!", diff);

        s.Print(0, 0, "aBcDefghi!");
        r.Render(s, flush: false);
        Assert.Equal("\e[1;1HaBcD", Take(w));
    }

    [Fact]
    public void StyleChangesAreTracked()
    {
        var (r, w) = NewRenderer();
        var s = new Surface(3, 1);
        r.Render(s, flush: false);
        Take(w);

        s.Print(0, 0, "ab", new Style(Color.Red));
        s.Print(2, 0, "c", new Style(Color.Red, default, TextAttributes.Bold));
        r.Render(s, flush: false);
        Assert.Equal("\e[1;1H\e[31mab\e[1mc\e[0m", Take(w));
    }

    [Fact]
    public void CursorIsPlacedAndShown()
    {
        var (r, w) = NewRenderer();
        var s = new Surface(4, 2);
        r.Cursor = (1, 1);
        r.Render(s, flush: false);
        Assert.EndsWith("\e[2;2H\e[?25h", Take(w));
        r.Render(s, flush: false);
        Assert.Equal("", Take(w)); // unchanged frame, cursor already there: nothing to send
    }

    [Fact]
    public void ResizeForcesFullRedraw()
    {
        var (r, w) = NewRenderer();
        var s = new Surface(4, 2);
        r.Render(s, flush: false);
        Take(w);
        s.Resize(5, 2);
        r.Render(s, flush: false);
        Assert.Contains("\e[2J", Take(w));
    }

    [Theory]
    [InlineData(ColorMode.TrueColor, 5)]
    [InlineData(ColorMode.Indexed256, 8)]
    public void ColorsThatLookTheSameOnScreenAreNotResent(ColorMode mode, int precision)
    {
        var w = new AnsiWriter { ColorMode = mode, TrueColorPrecision = precision };
        var r = new ScreenRenderer(w);
        var s = new Surface(new TerminalSize(4, 1));
        s.Fill(s.Bounds, ' ', new Style(Color.Default, Color.Rgb(200, 100, 6)));
        r.Render(s, false);
        w.Clear();
        s.Fill(s.Bounds, ' ', new Style(Color.Default, Color.Rgb(201, 100, 6)));
        r.Render(s, false);
        Assert.Equal(0, r.LastFrameCellsChanged);
    }
}
