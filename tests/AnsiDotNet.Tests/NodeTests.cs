namespace AnsiDotNet.Tests;

public class NodeTests
{
    private static readonly TimeSpan Ms100 = TimeSpan.FromMilliseconds(100);

    private static Stage Layout(Node root, int w = 80, int h = 24)
    {
        var stage = new Stage(root);
        stage.Layout(new Rect(0, 0, w, h));
        return stage;
    }

    [Fact]
    public void StackSolvesChildrenWithGapsAndMargins()
    {
        var a = new Node { Height = 3 };
        var b = new Node { Height = Length.Fill(), Margin = new Thickness(2, 1, 2, 1) };
        var c = new Node { Height = Length.Percent(25) };
        var root = new Node { Gap = 1 }.Add(a, b, c);
        Layout(root, 80, 24);
        Assert.Equal(new Rect(0, 0, 80, 3), a.Bounds);
        Assert.Equal(new Rect(0, 18, 80, 6), c.Bounds);
        Assert.Equal(new Rect(2, 5, 76, 11), b.Bounds);
    }

    [Fact]
    public void CrossAxisSizesAndAlignment()
    {
        var a = new Node { Width = 10, Height = 2, HorizontalAlign = Align.Center };
        var b = new Node { Width = Length.Percent(50), Height = 2, HorizontalAlign = Align.End };
        var root = new Node().Add(a, b);
        Layout(root, 40, 10);
        Assert.Equal(new Rect(15, 0, 10, 2), a.Bounds);
        Assert.Equal(new Rect(20, 2, 20, 2), b.Bounds);
    }

    [Fact]
    public void JustifyAndHiddenChildren()
    {
        var a = new Node { Width = 4 };
        var hidden = new Node { Width = 10, Visible = false };
        var b = new Node { Width = 4 };
        var root = new Node { Direction = Axis.Horizontal, Justify = Justify.Center, Gap = 2 }.Add(a, hidden, b);
        Layout(root, 20, 1);
        Assert.Equal(5, a.Bounds.X);
        Assert.Equal(11, b.Bounds.X);
    }

    [Fact]
    public void DockTakesEdgesInOrder()
    {
        var header = new Node { Dock = Edge.Top, Height = 1 };
        var footer = new Node { Dock = Edge.Bottom, Height = 2 };
        var side = new Node { Dock = Edge.Right, Width = 20 };
        var body = new Node();
        var root = new Node { Layout = LayoutKind.Dock }.Add(header, footer, side, body);
        Layout(root, 80, 24);
        Assert.Equal(new Rect(0, 0, 80, 1), header.Bounds);
        Assert.Equal(new Rect(0, 22, 80, 2), footer.Bounds);
        Assert.Equal(new Rect(60, 1, 20, 21), side.Bounds);
        Assert.Equal(new Rect(0, 1, 60, 21), body.Bounds);
    }

    [Fact]
    public void LayerPlacesAndBordersInset()
    {
        var dialog = new Node { Width = 30, Height = 10, HorizontalAlign = Align.Center, VerticalAlign = Align.Center, Border = BoxStyle.Single, Padding = new Thickness(1, 0) };
        var toast = new Node { Width = 20, Height = 3, HorizontalAlign = Align.End, VerticalAlign = Align.End, Margin = 1 };
        var root = new Node { Layout = LayoutKind.Layer }.Add(new Node(), dialog, toast);
        var stage = Layout(root, 80, 24);
        Assert.Equal(new Rect(25, 7, 30, 10), dialog.Bounds);
        Assert.Equal(new Rect(27, 8, 26, 8), dialog.ContentBounds);
        Assert.Equal(new Rect(59, 20, 20, 3), toast.Bounds);
        Assert.Same(toast, stage.HitTest(60, 21));
        Assert.Same(dialog, stage.HitTest(30, 10));
    }

    [Fact]
    public void AutoSizesFromText()
    {
        var label = new TextNode("hello world, this wraps") { Height = Length.Auto };
        var rest = new Node();
        var root = new Node().Add(label, rest);
        Layout(root, 12, 10);
        Assert.Equal(new Rect(0, 0, 12, 2), label.Bounds);
        Assert.Equal(2, rest.Bounds.Y);

        var chip = new TextNode("ok") { Width = Length.Auto, Height = 1, Border = BoxStyle.Single, Padding = new Thickness(1, 0) };
        var row = new Node { Direction = Axis.Horizontal }.Add(chip, new Node());
        Layout(row, 40, 3);
        Assert.Equal(6, chip.Bounds.Width);
    }

    [Fact]
    public void DrawsBackgroundBorderContentAndChildren()
    {
        var inner = new TextNode("hi", new Style(Color.Yellow)) { Height = 1 };
        var panel = new Node { Border = BoxStyle.Ascii, Title = "T", Background = new Style(default, Color.Blue) }.Add(inner);
        var stage = new Stage(panel);
        var s = new Surface(8, 4);
        stage.Render(s, TimeSpan.Zero);
        Assert.Equal("+- T --+", s.GetText(0));
        Assert.Equal("|hi    |", s.GetText(1));
        Assert.Equal(Color.Blue, s[3, 2].Style.Background);
        Assert.Equal(Color.Yellow, s[1, 1].Style.Foreground);
    }

    [Fact]
    public void ShowFadeRunsToCompletionAndCallsBack()
    {
        var node = new Node { Visible = false };
        var stage = new Stage(new Node().Add(node));
        bool done = false;
        node.Show(Effect.Fade, TimeSpan.FromMilliseconds(200), Ease.Linear, completed: () => done = true);
        Assert.True(node.Visible);
        Assert.Equal(0f, node.Opacity);
        Assert.True(stage.IsAnimating);
        stage.Update(Ms100);
        Assert.Equal(0.5f, node.Opacity, 3);
        stage.Update(Ms100);
        stage.Update(Ms100);
        Assert.True(done);
        Assert.False(stage.IsAnimating);
    }

    [Fact]
    public void HideExpandCollapsesAndSiblingsReflow()
    {
        var side = new Node { Width = 20 };
        var main = new Node();
        var root = new Node { Direction = Axis.Horizontal }.Add(side, main);
        var stage = new Stage(root);
        var area = new Rect(0, 0, 100, 10);
        stage.Layout(area);
        Assert.Equal(20, main.Bounds.X);

        side.Hide(Effect.Expand, TimeSpan.FromMilliseconds(200), Ease.Linear);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.Equal(10, side.Bounds.Width);
        Assert.Equal(10, main.Bounds.X);

        stage.Update(Ms100);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.False(side.Visible);
        Assert.Equal(1f, side.Extent); // reset for the next show
        Assert.Equal(new Rect(0, 0, 100, 10), main.Bounds);
    }

    [Fact]
    public void SlideStartsOutsideTheParentAndArrives()
    {
        var panel = new Node { Width = 10, Height = 4, HorizontalAlign = Align.End, Visible = false };
        var stage = new Stage(new Node { Layout = LayoutKind.Layer }.Add(panel));
        var area = new Rect(0, 0, 40, 10);
        panel.Show(Effect.SlideRight, TimeSpan.FromMilliseconds(200), Ease.Linear);
        stage.Layout(area);
        Assert.Equal(40, panel.DrawBounds.X);
        Assert.True(panel.VisibleBounds.IsEmpty);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.Equal(35, panel.DrawBounds.X);
        Assert.Equal(new Rect(35, 0, 5, 4), panel.VisibleBounds);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.Equal(30, panel.DrawBounds.X);
    }

    [Fact]
    public void WipeRevealsFromAnEdge()
    {
        var node = new Node { Visible = false };
        var stage = new Stage(new Node().Add(node));
        node.Show(Effect.WipeTop, TimeSpan.FromMilliseconds(200), Ease.Linear);
        stage.Update(Ms100);
        stage.Layout(new Rect(0, 0, 10, 10));
        Assert.Equal(new Rect(0, 0, 10, 5), node.VisibleBounds);
    }

    [Fact]
    public void HidingThenShowingReversesSmoothly()
    {
        var node = new Node();
        var stage = new Stage(new Node().Add(node));
        node.Hide(Effect.Fade, TimeSpan.FromMilliseconds(200), Ease.Linear);
        stage.Update(Ms100);
        Assert.Equal(0.5f, node.Opacity, 3);
        node.Show(Effect.None, TimeSpan.FromMilliseconds(200), Ease.Linear);
        Assert.Equal(0.5f, node.Opacity, 3); // picks up the interrupted fade
        stage.Update(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1f, node.Opacity);
        Assert.True(node.Visible);
    }

    [Fact]
    public void AnimateNextLayoutGlidesEverythingThatMoved()
    {
        var a = new Node { Height = 4 };
        var b = new Node { Height = 4 };
        var root = new Node().Add(a, b);
        var stage = new Stage(root);
        var area = new Rect(0, 0, 10, 20);
        stage.Layout(area);
        Assert.Equal(4, b.Bounds.Y);

        stage.AnimateNextLayout(TimeSpan.FromMilliseconds(200), Ease.Linear);
        a.Visible = false;
        stage.Layout(area);
        Assert.Equal(4, b.Bounds.Y);
        Assert.Equal(0, b.TargetBounds.Y);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.Equal(2, b.Bounds.Y);
        stage.Update(Ms100);
        stage.Layout(area);
        Assert.Equal(0, b.Bounds.Y);
    }

    [Fact]
    public void ResizeSnapsInsteadOfAnimating()
    {
        var a = new Node { LayoutTransition = Ms100 };
        var stage = new Stage(new Node().Add(a));
        stage.Layout(new Rect(0, 0, 10, 10));
        stage.Layout(new Rect(0, 0, 20, 10));
        Assert.Equal(20, a.Bounds.Width);
        Assert.False(stage.IsAnimating);
    }

    [Fact]
    public void UndockFliesIntoTheLayerAndDockReturns()
    {
        var side = new Node { Dock = Edge.Left, Width = 10 };
        var body = new Node();
        var dock = new Node { Layout = LayoutKind.Dock }.Add(side, body);
        var floating = new Node { Layout = LayoutKind.Layer };
        var root = new Node { Layout = LayoutKind.Layer }.Add(dock, floating);
        var stage = new Stage(root);
        var area = new Rect(0, 0, 100, 30);
        stage.Layout(area);
        Assert.Equal(new Rect(0, 0, 10, 30), side.Bounds);

        side.Undock(floating, 30, 10);
        stage.Layout(area);
        Assert.Same(floating, side.Parent);
        Assert.Equal(new Rect(0, 0, 10, 30), side.Bounds);       // starts where it was
        Assert.Equal(new Rect(35, 10, 30, 10), side.TargetBounds);
        Assert.Equal(new Rect(10, 0, 90, 30), body.Bounds);      // body glides too
        Assert.Equal(new Rect(0, 0, 100, 30), body.TargetBounds);
        stage.Update(TimeSpan.FromSeconds(1));
        stage.Layout(area);
        Assert.Equal(new Rect(35, 10, 30, 10), side.Bounds);
        Assert.Equal(new Rect(0, 0, 100, 30), body.Bounds);

        side.DockTo(dock, Edge.Left, 12);
        stage.Layout(area);
        Assert.Equal(new Rect(35, 10, 30, 10), side.Bounds);
        stage.Update(TimeSpan.FromSeconds(1));
        stage.Layout(area);
        Assert.Equal(new Rect(0, 0, 12, 30), side.Bounds);
    }

    [Fact]
    public void CallbacksMayChangeTheTree()
    {
        var root = new Node();
        var toast = new Node();
        root.Add(toast);
        var stage = new Stage(root);
        toast.Hide(Effect.Fade, Ms100, completed: toast.RemoveFromParent);
        stage.Update(TimeSpan.FromSeconds(1));
        Assert.Empty(root.Children);
    }

    [Fact]
    public void HideWithDelayAndNoEffect()
    {
        var node = new Node();
        var stage = new Stage(new Node().Add(node));
        node.Hide(delay: TimeSpan.FromMilliseconds(200));
        stage.Update(Ms100);
        Assert.True(node.Visible);
        Assert.Equal(1f, node.Opacity);
        stage.Update(Ms100);
        stage.Update(Ms100);
        Assert.False(node.Visible);
    }

    [Fact]
    public void OpacityBlendsOverWhatIsBehind()
    {
        var back = new Node { Background = new Style(default, Color.Rgb(200, 0, 0)) };
        var front = new Node { Background = new Style(default, Color.Rgb(0, 0, 200)), Opacity = 0.5f };
        var stage = new Stage(new Node { Layout = LayoutKind.Layer }.Add(back, front));
        var s = new Surface(4, 2);
        stage.Render(s, TimeSpan.Zero);
        Assert.Equal(Color.Rgb(100, 0, 100), s[1, 1].Style.Background);
    }

    [Fact]
    public void BlendCrossFadesGlyphs()
    {
        var dst = new Surface(3, 1);
        dst.Print(0, 0, "abc", new Style(Color.Rgb(255, 255, 255), Color.Rgb(0, 0, 0)));
        var src = new Surface(3, 1);
        src.Print(0, 0, "xbc", new Style(Color.Rgb(255, 255, 255), Color.Rgb(0, 0, 0)));
        var early = new Surface(3, 1);
        early.Blit(dst, 0, 0);
        early.Blend(src, src.Bounds, 0, 0, 0.25f);
        Assert.Equal("abc", early.GetText(0));        // old glyph still showing, dimmed
        Assert.Equal(Color.Rgb(127, 127, 127), early[0, 0].Style.Foreground);
        dst.Blend(src, src.Bounds, 0, 0, 0.75f);
        Assert.Equal("xbc", dst.GetText(0));
        Assert.Equal(Color.Rgb(127, 127, 127), dst[0, 0].Style.Foreground);
    }

    [Fact]
    public void FollowTailAndTypeIn()
    {
        var log = new TextNode("1\n2\n3") { FollowTail = true };
        var stage = new Stage(log);
        var s = new Surface(5, 2);
        stage.Render(s, TimeSpan.Zero);
        stage.Render(s, TimeSpan.FromSeconds(1));
        Assert.Equal("2    ", s.GetText(0));
        log.Text = "1\n2\n3\n4";
        stage.Render(s, TimeSpan.Zero);
        stage.Render(s, TimeSpan.FromSeconds(1));
        Assert.Equal("3    ", s.GetText(0));

        var typed = new TextNode("hello");
        var st2 = new Stage(typed);
        var s2 = new Surface(5, 1);
        bool finished = false;
        typed.TypeIn(10, completed: () => finished = true);
        st2.Render(s2, TimeSpan.FromMilliseconds(300));
        Assert.Equal("hel  ", s2.GetText(0));
        st2.Render(s2, TimeSpan.FromSeconds(1));
        Assert.Equal("hello", s2.GetText(0));
        Assert.True(finished);
    }

    [Fact]
    public void NeedsRenderCoversTheFinalFrame()
    {
        var node = new Node();
        var stage = new Stage(new Node().Add(node));
        var s = new Surface(4, 4);
        stage.Render(s, TimeSpan.Zero);
        Assert.False(stage.NeedsRender);
        node.Hide(Effect.Fade, Ms100);
        Assert.True(stage.NeedsRender);
        stage.Update(TimeSpan.FromSeconds(1));
        Assert.False(stage.IsAnimating);
        Assert.True(stage.NeedsRender); // finished, but not drawn yet
        stage.Layout(s.Bounds);
        stage.Draw(s);
        Assert.False(stage.NeedsRender);
    }

    [Fact]
    public void AutoHeightWrapsAtTheNodesOwnWidth()
    {
        // A fixed-width column of auto-height boxes inside a much wider layer, like a toast stack.
        var toast = new TextNode("one two three four five six seven eight nine ten") { Height = Length.Auto, Border = BoxStyle.Single, Padding = new Thickness(1, 0) };
        var column = new Node { Width = 16, Height = Length.Auto, HorizontalAlign = Align.End, VerticalAlign = Align.End }.Add(toast);
        var stage = new Stage(new Node { Layout = LayoutKind.Layer }.Add(column));
        var s = new Surface(80, 24);
        stage.Render(s, TimeSpan.Zero);
        // 12 columns of text: "one two" / "three four" / "five six" / "seven eight" / "nine ten" = 5 lines + border.
        Assert.Equal(7, toast.Bounds.Height);
        Assert.Equal(new Rect(64, 17, 16, 7), column.Bounds);
        Assert.Contains("nine ten", s.GetText(22));

        var half = new TextNode("aaaa bbbb cccc dddd") { Width = Length.Percent(50), Height = Length.Auto };
        var root = new Node { Layout = LayoutKind.Layer }.Add(half);
        new Stage(root).Layout(new Rect(0, 0, 20, 10));
        Assert.Equal(new Rect(0, 0, 10, 2), half.Bounds);
    }
}
