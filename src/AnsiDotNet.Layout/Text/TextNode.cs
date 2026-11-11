using System;

namespace AnsiDotNet.Layout;

/// <summary>
/// A node that shows a <see cref="TextBlock"/>. Measures itself for <see cref="Length.Auto"/>, scrolls smoothly,
/// can follow the end of growing text (logs, chat), and can type itself in.
/// </summary>
public class TextNode : Node
{
    private Tween _scroll = Tween.At(0);
    private Tween _typing = Tween.At(float.MaxValue);
    private Action? _typed;
    private bool _atTail = true;
    private int _maxScroll, _lastHeight;

    public TextNode(string text = "", Style style = default, string? name = null) : base(name) => Block = new TextBlock(text, style);

    public TextNode(RichText text, Style style = default, string? name = null) : base(name) => Block = new TextBlock(text, style);

    public TextBlock Block { get; }

    public string Text
    {
        get => Block.Text;
        set => Block.Text = value;
    }

    public RichText? Rich
    {
        get => Block.Rich;
        set => Block.Rich = value;
    }

    public Style TextStyle
    {
        get => Block.Style;
        set => Block.Style = value;
    }

    public TextAlign TextAlign
    {
        get => Block.Align;
        set => Block.Align = value;
    }

    public WrapMode Wrap
    {
        get => Block.Wrap;
        set => Block.Wrap = value;
    }

    /// <summary>Keep the last line in view as text is added, unless the user has scrolled away from the end.</summary>
    public bool FollowTail { get; set; }

    /// <summary>Furthest line <see cref="ScrollTo"/> can reach, as of the last draw.</summary>
    public int MaxScroll => _maxScroll;

    /// <summary>The first visible line (fractional while scrolling).</summary>
    public float ScrollPosition => _scroll.Value;

    public bool IsTyping => _typing.IsRunning;

    public TimeSpan ScrollDuration { get; set; } = TimeSpan.FromMilliseconds(120);

    public void ScrollTo(int line, bool animate = true)
    {
        line = Math.Clamp(line, 0, _maxScroll);
        _atTail = line >= _maxScroll;
        if (animate)
            _scroll.Retarget(line, ScrollDuration, Ease.OutQuad);
        else
            _scroll.Set(line);
    }

    public void ScrollBy(int lines, bool animate = true) => ScrollTo((int)MathF.Round(_scroll.To) + lines, animate);

    public void PageUp(bool animate = true) => ScrollBy(-Math.Max(1, _lastHeight - 1), animate);
    public void PageDown(bool animate = true) => ScrollBy(Math.Max(1, _lastHeight - 1), animate);

    public void ScrollToEnd(bool animate = true)
    {
        _atTail = true;
        ScrollTo(_maxScroll, animate);
    }

    /// <summary>Reveals the text a character at a time.</summary>
    public void TypeIn(float charsPerSecond = 60, TimeSpan delay = default, Action? completed = null)
    {
        var duration = TimeSpan.FromSeconds(Block.Length / Math.Max(1f, charsPerSecond));
        _typing = new Tween(0, Block.Length, duration, Ease.Linear, delay);
        _typed = completed;
    }

    /// <summary>Shows all text immediately, ending a <see cref="TypeIn"/>.</summary>
    public void FinishTyping() => _typing = Tween.At(float.MaxValue);

    protected override TerminalSize MeasureContent(int maxWidth, int maxHeight)
    {
        var m = Block.Measure(Math.Max(1, maxWidth));
        return new TerminalSize(m.Width, Math.Min(m.Height, Math.Max(0, maxHeight)));
    }

    protected override void OnRender(Surface surface, Rect area)
    {
        _lastHeight = area.Height;
        int lines = Block.LineCount(area.Width);
        int max = Math.Max(0, lines - area.Height);
        if (max != _maxScroll)
        {
            _maxScroll = max;
            if (FollowTail && _atTail)
                _scroll.Retarget(max, ScrollDuration, Ease.OutQuad);
            else if (_scroll.To > max)
                _scroll.Set(max);
        }
        Block.ScrollY = (int)MathF.Round(Math.Clamp(_scroll.Value, 0, max));
        Block.VisibleCharacters = _typing.IsRunning ? (int)_typing.Value : int.MaxValue;
        Block.Draw(surface, area);
    }

    protected override bool UpdateContent(TimeSpan elapsed)
    {
        bool scrolling = _scroll.Update(elapsed);
        bool typing = _typing.IsRunning && _typing.Update(elapsed);
        if (!typing && _typed is { } done)
        {
            _typed = null;
            _typing = Tween.At(float.MaxValue);
            Defer(done);
        }
        return scrolling || typing;
    }

    protected override bool IsContentAnimating => _scroll.IsRunning || _typing.IsRunning;
}
