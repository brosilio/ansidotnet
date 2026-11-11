namespace AnsiDotNet.Tests;

public class TextLayoutTests
{
    private static string[] Wrap(string text, int width, WrapMode mode = WrapMode.Word)
    {
        var lines = new List<string>();
        foreach (var l in TextLayout.Lines(text, width, mode))
            lines.Add(text.Substring(l.Start, l.Length));
        return lines.ToArray();
    }

    [Fact]
    public void WrapsAtWordsAndTrimsTrailingSpaces()
    {
        Assert.Equal(["the quick", "brown fox", "jumps"], Wrap("the quick brown fox jumps", 10));
        Assert.Equal(["aaaa", "bbbb"], Wrap("aaaa    bbbb", 6));
        Assert.Equal(["  indented", "text"], Wrap("  indented text", 10));
    }

    [Fact]
    public void SplitsWordsThatCannotFit()
    {
        Assert.Equal(["abcde", "fghij", "k"], Wrap("abcdefghijk", 5));
        Assert.Equal(["ab", "abcde", "fg"], Wrap("ab abcdefg", 5));
        Assert.Equal(["abc d", "ef gh"], Wrap("abc def gh", 5, WrapMode.Character));
    }

    [Fact]
    public void BreaksAfterHyphensAndBetweenWideCharacters()
    {
        Assert.Equal(["well-", "known"], Wrap("well-known", 7));
        Assert.Equal(["日本語", "です"], Wrap("日本語です", 6));
        var line = new LineBreaker("日本語です", 6).GetEnumerator();
        Assert.True(line.MoveNext());
        Assert.Equal(6, line.Current.Width);
        Assert.True(line.Current.Wrapped);
    }

    [Fact]
    public void NewlinesAndEmptyText()
    {
        Assert.Empty(Wrap("", 10));
        Assert.Equal(["a", "", "b"], Wrap("a\n\nb", 10));
        Assert.Equal(["a", ""], Wrap("a\n", 10));
        Assert.Equal(["word", "next"], Wrap("word   \nnext", 4));
        Assert.Equal(["no wrap at all"], Wrap("no wrap at all", 3, WrapMode.None));
    }

    [Fact]
    public void TabsAdvanceToStops()
    {
        Assert.Equal(new TerminalSize(5, 1), TextLayout.Measure("ab\tc", 80));
        Assert.Equal(new TerminalSize(9, 1), TextLayout.Measure("\t\tx", 80));
    }

    private static Surface Draw(TextBlock block, int w, int h)
    {
        var s = new Surface(w, h);
        block.Draw(s, s.Bounds);
        return s;
    }

    [Fact]
    public void AlignsAndJustifies()
    {
        Assert.Equal("  hi  ", Draw(new TextBlock("hi") { Align = TextAlign.Center }, 6, 1).GetText(0));
        Assert.Equal("    hi", Draw(new TextBlock("hi") { Align = TextAlign.Right }, 6, 1).GetText(0));
        var j = Draw(new TextBlock("aa b cc dd") { Align = TextAlign.Justify }, 8, 2);
        Assert.Equal("aa  b cc", j.GetText(0));
        Assert.Equal("dd      ", j.GetText(1)); // last line stays ragged
    }

    [Fact]
    public void EllipsisScrollAndReveal()
    {
        var s = Draw(new TextBlock("one two three four") { Ellipsis = true }, 9, 1);
        Assert.Equal("one two…", s.GetText(0).TrimEnd());
        Assert.Equal("long tex…", Draw(new TextBlock("long text here") { Wrap = WrapMode.None, Ellipsis = true }, 9, 1).GetText(0));

        var scrolled = Draw(new TextBlock("a\nb\nc\nd") { ScrollY = 2 }, 3, 2);
        Assert.Equal("c  ", scrolled.GetText(0));
        Assert.Equal("d  ", scrolled.GetText(1));

        Assert.Equal("hel  ", Draw(new TextBlock("hello") { VisibleCharacters = 3 }, 5, 1).GetText(0));
        var bottom = Draw(new TextBlock("x") { VerticalAlign = Align.End }, 1, 3);
        Assert.Equal("x", bottom.GetText(2));
    }

    [Fact]
    public void RichRunsInheritBaseColors()
    {
        var rich = new RichText().Append("ab", new Style(Color.Red)).Append("cd");
        var block = new TextBlock(rich, new Style(Color.White, Color.Blue));
        var s = Draw(block, 4, 1);
        Assert.Equal(new Style(Color.Red, Color.Blue), s[0, 0].Style);
        Assert.Equal(new Style(Color.White, Color.Blue), s[2, 0].Style);

        rich.RemoveStart(3);
        Assert.Equal("d", rich.ToString());
        Assert.Equal(default, rich.StyleAt(0));
        Assert.Single(rich.Runs.ToArray());
    }

    [Fact]
    public void CachedLinesFollowEdits()
    {
        var rich = new RichText("short");
        var block = new TextBlock(rich);
        Assert.Equal(1, block.LineCount(10));
        rich.Append(" and now much longer");
        Assert.Equal(3, block.LineCount(10));
        block.Text = "x";
        Assert.Equal(1, block.LineCount(10));
    }
}
