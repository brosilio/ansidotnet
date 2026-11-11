namespace AnsiDotNet.Tests;

public class LayoutMathTests
{
    private static float[] Solve(int available, params Length[] lengths)
    {
        var sizes = new float[lengths.Length];
        LengthSolver.Solve(available, lengths, default, default, sizes);
        return sizes;
    }

    [Fact]
    public void FixedFirstThenFillsShareByWeight()
    {
        Assert.Equal([10f, 30f, 60f], Solve(100, 10, Length.Fill(1), Length.Fill(2)));
        Assert.Equal([25f, 75f], Solve(100, Length.Percent(25), Length.Fill()));
        Assert.Equal([20f, 20f, 20f], Solve(60, Length.Ratio(1, 3), Length.Fill(), Length.Fill()));
    }

    [Fact]
    public void FillMaximumHandsTheRestToOthers()
    {
        Assert.Equal([10f, 90f], Solve(100, Length.Fill().AtMost(10), Length.Fill()));
        Assert.Equal([10f, 20f], Solve(100, Length.Fill().AtMost(10), Length.Fill().AtMost(20)));
    }

    [Fact]
    public void ShrinksFillsBeforeFixed()
    {
        Assert.Equal([5f, 30f], Solve(35, Length.Fill().AtLeast(5), 30));
        // Fill already at its minimum; fixed items give up the rest in proportion.
        var s = Solve(25, Length.Fill().AtLeast(5), 10, 30);
        Assert.Equal(5f, s[0]);
        Assert.Equal(20f, s[1] + s[2], 3);
        Assert.Equal(s[1] * 3, s[2], 3);
    }

    [Fact]
    public void ScaleCollapsesItemsAndOthersFillIn()
    {
        var sizes = new float[3];
        LengthSolver.Solve(100, [Length.Fill(), 20, Length.Fill()], default, [1f, 0.5f, 0f], sizes);
        Assert.Equal([90f, 10f, 0f], sizes);
    }

    [Fact]
    public void RoundedEdgesAreContiguous()
    {
        Span<Rect> r = stackalloc Rect[3];
        new Rect(0, 0, 10, 1).Split(Axis.Horizontal, [Length.Fill(), Length.Fill(), Length.Fill()], r);
        Assert.Equal(0, r[0].X);
        Assert.Equal(r[0].Right, r[1].X);
        Assert.Equal(r[1].Right, r[2].X);
        Assert.Equal(10, r[2].Right);
    }

    [Fact]
    public void SplitHelpers()
    {
        var area = new Rect(0, 0, 80, 24);
        var (header, body, footer) = area.SplitRows(1, Length.Fill(), 2);
        Assert.Equal(new Rect(0, 0, 80, 1), header);
        Assert.Equal(new Rect(0, 1, 80, 21), body);
        Assert.Equal(new Rect(0, 22, 80, 2), footer);

        Span<Rect> cols = stackalloc Rect[2];
        body.Split(Axis.Horizontal, [Length.Fill(), 20], cols, gap: 1);
        Assert.Equal(new Rect(0, 1, 59, 21), cols[0]);
        Assert.Equal(new Rect(60, 1, 20, 21), cols[1]);
        Assert.Equal(new Rect(40, 1, 40, 21), body.SplitColumns(Length.Fill(), Length.Fill()).Item2);

        Span<Rect> three = stackalloc Rect[3];
        new Rect(0, 0, 20, 1).Split(Axis.Horizontal, [4, 4, 4], three, justify: Justify.SpaceBetween);
        Assert.Equal([0, 8, 16], new[] { three[0].X, three[1].X, three[2].X });

        var top = area.Take(Edge.Top, 3, out var rest);
        Assert.Equal(new Rect(0, 0, 80, 3), top);
        Assert.Equal(new Rect(0, 3, 80, 21), rest);
        Assert.Equal(new Rect(70, 0, 10, 24), area.Take(Edge.Right, 10, out _));

        Assert.Equal(new Rect(30, 10, 20, 4), area.Place(20, 4));
        Assert.Equal(new Rect(60, 20, 20, 4), area.Place(20, 4, Align.End, Align.End));
        Assert.Equal(new Rect(2, 1, 76, 20), area.Inset(new Thickness(2, 1, 2, 3)));
    }

    [Fact]
    public void RectLerpMovesEachEdge()
    {
        var a = new Rect(0, 0, 10, 10);
        var b = new Rect(10, 0, 20, 10);
        Assert.Equal(a, a.Lerp(b, 0));
        Assert.Equal(b, a.Lerp(b, 1));
        Assert.Equal(new Rect(5, 0, 15, 10), a.Lerp(b, 0.5f));
    }

    [Fact]
    public void TweenEasesAndRetargetsFromCurrentValue()
    {
        var t = new Tween(0, 10, TimeSpan.FromSeconds(1), Ease.Linear);
        Assert.True(t.IsRunning);
        t.Update(TimeSpan.FromSeconds(0.5));
        Assert.Equal(5f, t.Value, 3);
        t.Retarget(0, TimeSpan.FromSeconds(1), Ease.Linear);
        Assert.Equal(5f, t.Value, 3);
        t.Update(TimeSpan.FromSeconds(0.5));
        Assert.Equal(2.5f, t.Value, 3);
        Assert.False(t.Update(TimeSpan.FromSeconds(1)));
        Assert.Equal(0f, t.Value);

        var delayed = new Tween(0, 1, TimeSpan.FromSeconds(1), Ease.Linear, TimeSpan.FromSeconds(1));
        delayed.Update(TimeSpan.FromSeconds(0.9));
        Assert.Equal(0f, delayed.Value);
        delayed.Update(TimeSpan.FromSeconds(0.6));
        Assert.Equal(0.5f, delayed.Value, 3);
    }

    [Theory]
    [InlineData(Ease.Linear)]
    [InlineData(Ease.OutCubic)]
    [InlineData(Ease.InOutQuart)]
    [InlineData(Ease.OutBack)]
    [InlineData(Ease.OutElastic)]
    [InlineData(Ease.OutBounce)]
    [InlineData(Ease.InOutExpo)]
    public void EasesStartAtZeroAndEndAtOne(Ease ease)
    {
        Assert.Equal(0f, Easing.Apply(ease, 0));
        Assert.Equal(1f, Easing.Apply(ease, 1));
        Assert.InRange(Easing.Apply(ease, 0.999f), 0.95f, 1.05f);
    }
}
