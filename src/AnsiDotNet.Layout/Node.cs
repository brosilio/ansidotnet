using System;
using System.Collections.Generic;

namespace AnsiDotNet.Layout;

/// <summary>
/// A rectangle in a layout tree. A node sizes itself within its parent (<see cref="Width"/>, <see cref="Height"/>,
/// <see cref="Margin"/>, alignment), arranges its children (<see cref="Layout"/>), optionally draws a background,
/// border and content, and carries animation state. Subclass and override <see cref="OnRender"/> /
/// <see cref="MeasureContent"/>, or just set <see cref="Content"/>.
/// </summary>
public partial class Node
{
    private readonly List<Node> _children = new();
    internal Stage? _stage;

    // Filled during layout. _slot is scratch space: the rect the parent arranged this node into.
    private Rect _slot;
    private bool _laidOut;

    public Node(string? name = null) => Name = name;

    public string? Name { get; set; }

    /// <summary>Free for the app to use.</summary>
    public object? Tag { get; set; }

    #region Tree

    public Node? Parent { get; private set; }
    public IReadOnlyList<Node> Children => _children;

    /// <summary>The stage this node's tree is attached to, if any.</summary>
    public Stage? Stage
    {
        get
        {
            var n = this;
            while (n.Parent is not null)
                n = n.Parent;
            return n._stage;
        }
    }

    /// <summary>Appends children. Returns this node, so trees can be built in one expression.</summary>
    public Node Add(params ReadOnlySpan<Node> children)
    {
        foreach (var c in children)
            Insert(_children.Count, c);
        return this;
    }

    public void Insert(int index, Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child._stage is not null)
            throw new InvalidOperationException("A stage's root can't be added to another node.");
        for (var p = this; p is not null; p = p.Parent)
        {
            if (p == child)
                throw new InvalidOperationException("A node can't be added to itself or its descendants.");
        }
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Insert(Math.Clamp(index, 0, _children.Count), child);
    }

    public bool Remove(Node child)
    {
        if (child.Parent != this)
            return false;
        _children.Remove(child);
        child.Parent = null;
        child.Forget();
        return true;
    }

    public void RemoveFromParent() => Parent?.Remove(this);

    public void ClearChildren()
    {
        foreach (var c in _children)
        {
            c.Parent = null;
            c.Forget();
        }
        _children.Clear();
    }

    /// <summary>
    /// Moves this node to another parent. With <paramref name="animate"/>, it travels from where it is on screen to its
    /// new place, and everything that reflows because of the move animates too. This is how docking and undocking work.
    /// </summary>
    public void MoveTo(Node parent, int index = int.MaxValue, bool animate = true, TimeSpan? duration = null, Ease? ease = null)
    {
        if (animate)
            (Stage ?? parent.Stage)?.AnimateNextLayout(duration, ease);
        parent.Insert(index, this);
    }

    /// <summary>Moves to the end of the parent's children, which draws on top in a layer.</summary>
    public void BringToFront()
    {
        if (Parent is { } p && p._children[^1] != this)
        {
            p._children.Remove(this);
            p._children.Add(this);
        }
    }

    /// <summary>Depth-first search of this subtree by <see cref="Name"/>.</summary>
    public Node? Find(string name)
    {
        if (Name == name)
            return this;
        foreach (var c in _children)
        {
            if (c.Find(name) is { } hit)
                return hit;
        }
        return null;
    }

    private void Forget()
    {
        _laidOut = false;
        foreach (var c in _children)
            c.Forget();
    }

    #endregion

    #region Layout properties

    /// <summary>How children are arranged.</summary>
    public LayoutKind Layout { get; set; } = LayoutKind.Stack;

    /// <summary>The main axis of a <see cref="LayoutKind.Stack"/>.</summary>
    public Axis Direction { get; set; } = Axis.Vertical;

    public Length Width { get; set; } = Length.Fill();
    public Length Height { get; set; } = Length.Fill();

    /// <summary>Space outside the node, taken from its slot.</summary>
    public Thickness Margin { get; set; }

    /// <summary>Space inside the border, around content and children.</summary>
    public Thickness Padding { get; set; }

    /// <summary>Cells between children of a stack.</summary>
    public int Gap { get; set; }

    /// <summary>Where a stack puts space its children leave unused.</summary>
    public Justify Justify { get; set; }

    /// <summary>Position within the slot when narrower than it.</summary>
    public Align HorizontalAlign { get; set; }

    /// <summary>Position within the slot when shorter than it.</summary>
    public Align VerticalAlign { get; set; }

    /// <summary>In a <see cref="LayoutKind.Dock"/> parent: the edge this node takes a slice from. Null shares the remaining space.</summary>
    public Edge? Dock { get; set; }

    /// <summary>Hidden nodes take no space and don't draw. See also <see cref="Show"/> and <see cref="Hide"/>.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Keep children's drawing inside this node's content area. On by default.</summary>
    public bool ClipChildren { get; set; } = true;

    /// <summary>Whether <see cref="Stage.HitTest"/> can return this node. Its children are still tested.</summary>
    public bool HitTestVisible { get; set; } = true;

    #endregion

    #region Appearance

    /// <summary>Fills the node before anything else is drawn.</summary>
    public Style? Background { get; set; }

    /// <summary>Draws a one-cell border; content and children go inside it.</summary>
    public BoxStyle? Border { get; set; }
    public Style BorderStyle { get; set; }
    public string? Title { get; set; }
    public Style? TitleStyle { get; set; }

    /// <summary>Draws the node's content into its content area (inside border and padding), clipped to it.</summary>
    public Action<Surface, Rect>? Content { get; set; }

    /// <summary>Reports the content size for <see cref="Length.Auto"/>, given the maximum width and height.</summary>
    public Func<int, int, TerminalSize>? Measurer { get; set; }

    #endregion

    #region Results

    /// <summary>Where the node is this frame, including layout transitions but not offsets or slides.</summary>
    public Rect Bounds { get; private set; }

    /// <summary>Where layout wants the node; differs from <see cref="Bounds"/> while a layout transition runs.</summary>
    public Rect TargetBounds { get; private set; }

    /// <summary>Where the node is drawn: <see cref="Bounds"/> moved by offsets, slides and shakes.</summary>
    public Rect DrawBounds { get; private set; }

    /// <summary>The drawn area inside the border and padding.</summary>
    public Rect ContentBounds { get; private set; }

    /// <summary>The part of the node that is actually visible: clipped by ancestors and any wipe.</summary>
    public Rect VisibleBounds { get; private set; }

    #endregion

    #region Drawing

    protected virtual void OnRender(Surface surface, Rect area) => Content?.Invoke(surface, area);

    internal void Draw(Surface target, int tx, int ty, Stage stage)
    {
        if (!Visible || (VisibleBounds.IsEmpty && ClipChildren))
            return;
        float opacity = Opacity;
        if (opacity <= 0.001f)
            return;
        if (opacity >= 0.999f)
        {
            DrawSelf(target, tx, ty, stage);
            return;
        }

        // Draw over a copy of what's behind, then mix the result back in.
        var r = VisibleBounds.Offset(tx, ty).Intersect(target.Bounds);
        if (r.IsEmpty)
            return;
        var temp = stage.Rent(r.Width, r.Height);
        temp.Blit(target, r, 0, 0);
        DrawSelf(temp, tx - r.X, ty - r.Y, stage);
        var saved = target.Clip;
        target.Clip = r;
        target.Blend(temp, temp.Bounds, r.X, r.Y, opacity, stage.DefaultForeground, stage.DefaultBackground);
        target.Clip = saved;
        stage.Return();
    }

    private void DrawSelf(Surface s, int tx, int ty, Stage stage)
    {
        var clip = VisibleBounds.Offset(tx, ty);
        var bounds = DrawBounds.Offset(tx, ty);
        s.Clip = clip;
        if (Background is { } bg)
            s.Fill(bounds, ' ', bg);
        if (Border is { } box)
            s.DrawBox(bounds, BorderStyle, box, Title, TitleStyle);
        var content = ContentBounds.Offset(tx, ty);
        s.Clip = clip.Intersect(content);
        if (!s.Clip.IsEmpty)
            OnRender(s, content);
        foreach (var c in _children)
            c.Draw(s, tx, ty, stage);
    }

    internal Node? HitTest(int x, int y)
    {
        if (!Visible || Opacity <= 0.001f)
            return null;
        bool inside = VisibleBounds.Contains(x, y);
        if (inside || !ClipChildren)
        {
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                if (_children[i].HitTest(x, y) is { } hit)
                    return hit;
            }
        }
        return inside && HitTestVisible ? this : null;
    }

    #endregion

    #region Measure

    private Thickness Chrome => Border is null ? Padding : Padding + 1;

    /// <summary>The size this node wants (excluding margin), given the space available. Used for <see cref="Length.Auto"/>.</summary>
    public TerminalSize Measure(int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(0, maxWidth);
        maxHeight = Math.Max(0, maxHeight);
        // Content is measured within the size this node will actually have, not everything offered: a fixed-width
        // box of wrapped text must report the height of the text wrapped at that width.
        int limitW = Limit(Width, maxWidth), limitH = Limit(Height, maxHeight);
        var chrome = Chrome;
        var c = MeasureContent(Math.Max(0, limitW - chrome.Horizontal), Math.Max(0, limitH - chrome.Vertical));
        int w = Width.Kind is LengthKind.Fixed or LengthKind.Fraction ? limitW : Math.Clamp(c.Width + chrome.Horizontal, Width.Min, limitW);
        int h = Height.Kind is LengthKind.Fixed or LengthKind.Fraction ? limitH : Math.Clamp(c.Height + chrome.Vertical, Height.Min, limitH);
        return new TerminalSize(Math.Min(w, maxWidth), Math.Min(h, maxHeight));

        static int Limit(Length l, int max)
        {
            float v = l.Kind switch
            {
                LengthKind.Fixed => l.Value,
                LengthKind.Fraction => l.Value * max,
                _ => max,
            };
            return Math.Min(max, (int)Math.Clamp(v, l.Min, l.Max));
        }
    }

    /// <summary>Content size for <see cref="Length.Auto"/>. By default: <see cref="Measurer"/> if set, otherwise what the children need.</summary>
    protected virtual TerminalSize MeasureContent(int maxWidth, int maxHeight)
    {
        if (Measurer is not null)
            return Measurer(maxWidth, maxHeight);
        return Layout switch
        {
            LayoutKind.Stack => MeasureStack(maxWidth, maxHeight),
            LayoutKind.Dock => MeasureDock(maxWidth, maxHeight),
            _ => MeasureLayer(maxWidth, maxHeight),
        };
    }

    private TerminalSize MeasureStack(int maxW, int maxH)
    {
        bool h = Direction == Axis.Horizontal;
        float main = 0, seen = 0;
        int cross = 0;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            float e = c.Extent;
            var d = c.Measure(maxW - c.Margin.Horizontal, maxH - c.Margin.Vertical);
            main += Gap * e * Math.Min(1, seen);
            main += ((h ? d.Width + c.Margin.Horizontal : d.Height + c.Margin.Vertical)) * e;
            cross = Math.Max(cross, h ? d.Height + c.Margin.Vertical : d.Width + c.Margin.Horizontal);
            seen += e;
        }
        int m = (int)MathF.Ceiling(main - 0.001f);
        return h ? new TerminalSize(m, cross) : new TerminalSize(cross, m);
    }

    private TerminalSize MeasureDock(int maxW, int maxH)
    {
        float accW = 0, accH = 0, w = 0, h = 0;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            float e = c.Extent;
            var d = c.Measure(Math.Max(0, maxW - (int)accW) - c.Margin.Horizontal, Math.Max(0, maxH - (int)accH) - c.Margin.Vertical);
            float dw = d.Width + c.Margin.Horizontal, dh = d.Height + c.Margin.Vertical;
            switch (c.Dock)
            {
                case Edge.Left or Edge.Right:
                    h = Math.Max(h, accH + dh);
                    accW += dw * e;
                    break;
                case Edge.Top or Edge.Bottom:
                    w = Math.Max(w, accW + dw);
                    accH += dh * e;
                    break;
                default:
                    w = Math.Max(w, accW + dw);
                    h = Math.Max(h, accH + dh);
                    break;
            }
        }
        return new TerminalSize((int)MathF.Ceiling(Math.Max(w, accW)), (int)MathF.Ceiling(Math.Max(h, accH)));
    }

    private TerminalSize MeasureLayer(int maxW, int maxH)
    {
        int w = 0, h = 0;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            var d = c.Measure(maxW - c.Margin.Horizontal, maxH - c.Margin.Vertical);
            w = Math.Max(w, d.Width + c.Margin.Horizontal);
            h = Math.Max(h, d.Height + c.Margin.Vertical);
        }
        return new TerminalSize(w, h);
    }

    #endregion

    #region Arrange

    internal readonly struct Pass(bool resized, bool animateAll, TimeSpan duration, Ease ease)
    {
        public readonly bool Resized = resized, AnimateAll = animateAll;
        public readonly TimeSpan Duration = duration;
        public readonly Ease Ease = ease;
    }

    // Pass 1: where everything should be. Starts layout transitions for nodes whose target moved.
    internal void ArrangeTarget(Rect rect, in Pass pass, bool ancestorMoving)
    {
        bool changed = _laidOut && rect != TargetBounds;
        TargetBounds = rect;
        if (changed && !pass.Resized && !ancestorMoving)
        {
            if (pass.AnimateAll)
                StartMove(pass.Duration, pass.Ease);
            else if (LayoutTransition is { } d)
                StartMove(d, LayoutEase);
        }
        _laidOut = true;

        ArrangeChildren(rect);
        bool moving = ancestorMoving || _ch[RectChannel].T.IsRunning;
        foreach (var c in _children)
        {
            if (c.Visible)
                c.ArrangeTarget(c._slot, pass, moving);
            else
                c.Forget();
        }
    }

    private void StartMove(TimeSpan duration, Ease ease)
    {
        _moveFrom = Bounds;
        _ch[RectChannel].T = new Tween(0, 1, duration, ease);
    }

    // Pass 2: where everything is this frame. Children of a node that is mid-transition are re-arranged inside its
    // current rect, so content reflows as a panel grows instead of lagging behind it.
    internal void Place(Rect arranged, float parentX, float parentY, Rect parentClip, Rect slideArea)
    {
        Bounds = _ch[RectChannel].T.IsRunning ? _moveFrom.Lerp(arranged, _ch[RectChannel].T.Value) : arranged;

        float ox = parentX + OffsetX, oy = parentY + OffsetY;
        if (_ch[ShakeChannel].T.IsRunning)
        {
            float p = _ch[ShakeChannel].T.Value;
            ox += _shakeAmplitude * MathF.Sin(p * MathF.PI * 8) * (1 - p);
        }
        float slide = Slide;
        if (slide < 1)
        {
            var b = Bounds.Offset(LengthSolver.Round(ox), LengthSolver.Round(oy));
            float k = 1 - slide;
            switch (SlideEdge)
            {
                case Edge.Left: ox -= (b.Right - slideArea.Left) * k; break;
                case Edge.Right: ox += (slideArea.Right - b.Left) * k; break;
                case Edge.Top: oy -= (b.Bottom - slideArea.Top) * k; break;
                default: oy += (slideArea.Bottom - b.Top) * k; break;
            }
        }
        DrawBounds = Bounds.Offset(LengthSolver.Round(ox), LengthSolver.Round(oy));
        ContentBounds = DrawBounds.Inset(Chrome);

        var visible = DrawBounds;
        float reveal = Reveal;
        if (reveal < 1)
        {
            int w = LengthSolver.Round(visible.Width * Math.Max(0, reveal));
            int h = LengthSolver.Round(visible.Height * Math.Max(0, reveal));
            visible = RevealEdge switch
            {
                Edge.Left => visible with { Width = w },
                Edge.Right => new Rect(visible.Right - w, visible.Y, w, visible.Height),
                Edge.Top => visible with { Height = h },
                _ => new Rect(visible.X, visible.Bottom - h, visible.Width, h),
            };
        }
        VisibleBounds = visible.Intersect(parentClip);

        var childClip = ClipChildren ? VisibleBounds.Intersect(ContentBounds) : parentClip;
        var childSlide = ClipChildren ? ContentBounds : slideArea;
        bool rearrange = Bounds != TargetBounds;
        if (rearrange)
            ArrangeChildren(Bounds);
        foreach (var c in _children)
        {
            if (c.Visible)
                c.Place(rearrange ? c._slot : c.TargetBounds, ox, oy, childClip, childSlide);
        }
    }

    private void ArrangeChildren(Rect rect)
    {
        if (_children.Count == 0)
            return;
        var content = rect.Inset(Chrome);
        switch (Layout)
        {
            case LayoutKind.Stack: ArrangeStack(content); break;
            case LayoutKind.Dock: ArrangeDock(content); break;
            default:
                foreach (var c in _children)
                {
                    if (c.Visible)
                        c._slot = c.PlaceIn(content);
                }
                break;
        }
    }

    private void ArrangeStack(Rect content)
    {
        int n = 0;
        foreach (var c in _children)
        {
            if (c.Visible)
                n++;
        }
        if (n == 0)
            return;

        bool h = Direction == Axis.Horizontal;
        int main = h ? content.Width : content.Height;
        int cross = h ? content.Height : content.Width;
        bool small = n <= 64;
        Span<Length> lengths = small ? stackalloc Length[n] : new Length[n];
        Span<int> measured = small ? stackalloc int[n] : new int[n];
        Span<float> scale = small ? stackalloc float[n] : new float[n];
        Span<float> before = small ? stackalloc float[n] : new float[n];
        Span<float> after = small ? stackalloc float[n] : new float[n];
        Span<int> pos = small ? stackalloc int[n] : new int[n];
        Span<int> size = small ? stackalloc int[n] : new int[n];

        int k = 0;
        float seen = 0;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            float e = c.Extent;
            var m = c.Margin;
            var len = h ? c.Width : c.Height;
            lengths[k] = len;
            measured[k] = len.Kind == LengthKind.Auto
                ? (h ? c.Measure(main - m.Horizontal, cross - m.Vertical).Width : c.Measure(cross - m.Horizontal, main - m.Vertical).Height)
                : 0;
            scale[k] = e;
            // A gap fades with the item after it, and only exists once something before it has appeared.
            before[k] = Gap * e * Math.Min(1, seen) + (h ? m.Left : m.Top) * e;
            after[k] = (h ? m.Right : m.Bottom) * e;
            seen += e;
            k++;
        }

        LengthSolver.Arrange(h ? content.X : content.Y, main, lengths, measured, scale, before, after, Justify, pos, size);

        k = 0;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            var m = c.Margin;
            int cs0 = h ? m.Top : m.Left;
            int avail = Math.Max(0, cross - (h ? m.Vertical : m.Horizontal));
            var clen = h ? c.Height : c.Width;
            int cm = clen.Kind == LengthKind.Auto
                ? (h ? c.Measure(size[k], avail).Height : c.Measure(avail, size[k]).Width)
                : 0;
            int cs = Math.Min(avail, clen.ResolveAlone(avail, cm));
            int cp = (h ? content.Y : content.X) + cs0 + RectExtensions.Offset(avail - cs, h ? c.VerticalAlign : c.HorizontalAlign);
            c._slot = h ? new Rect(pos[k], cp, size[k], cs) : new Rect(cp, pos[k], cs, size[k]);
            k++;
        }
    }

    private void ArrangeDock(Rect content)
    {
        float l = content.X, t = content.Y, r = content.Right, b = content.Bottom;
        foreach (var c in _children)
        {
            if (!c.Visible)
                continue;
            if (c.Dock is not { } edge)
            {
                int x0 = LengthSolver.Round(l), y0 = LengthSolver.Round(t);
                var rest = new Rect(x0, y0, Math.Max(0, LengthSolver.Round(r) - x0), Math.Max(0, LengthSolver.Round(b) - y0));
                c._slot = c.PlaceIn(rest);
                continue;
            }

            bool vertical = edge is Edge.Top or Edge.Bottom;
            var m = c.Margin;
            float e = c.Extent;
            int availMain = Math.Max(0, (int)(vertical ? b - t : r - l));
            int availCross = Math.Max(0, (int)(vertical ? r - l : b - t));
            int mMain = vertical ? m.Vertical : m.Horizontal;
            int mCross = vertical ? m.Horizontal : m.Vertical;
            var len = vertical ? c.Height : c.Width;
            int measured = len.Kind == LengthKind.Auto
                ? (vertical ? c.Measure(availCross - mCross, availMain - mMain).Height : c.Measure(availMain - mMain, availCross - mCross).Width)
                : 0;
            float size = Math.Min(Math.Max(0, availMain - mMain), len.ResolveAlone(availMain - mMain, measured));
            float total = (size + mMain) * e;

            float start;
            switch (edge)
            {
                case Edge.Left: start = l; l += total; break;
                case Edge.Right: r -= total; start = r; break;
                case Edge.Top: start = t; t += total; break;
                default: b -= total; start = b; break;
            }
            int a = LengthSolver.Round(start + (vertical ? m.Top : m.Left) * e);
            int z = LengthSolver.Round(start + total - (vertical ? m.Bottom : m.Right) * e);

            var clen = vertical ? c.Width : c.Height;
            int crossAvail = Math.Max(0, availCross - mCross);
            int cm = clen.Kind == LengthKind.Auto
                ? (vertical ? c.Measure(crossAvail, z - a).Width : c.Measure(z - a, crossAvail).Height)
                : 0;
            int cs = Math.Min(crossAvail, clen.ResolveAlone(crossAvail, cm));
            float crossStart = vertical ? l : t;
            int cp = LengthSolver.Round(crossStart) + (vertical ? m.Left : m.Top) + RectExtensions.Offset(crossAvail - cs, vertical ? c.HorizontalAlign : c.VerticalAlign);
            c._slot = vertical ? new Rect(cp, a, cs, Math.Max(0, z - a)) : new Rect(a, cp, Math.Max(0, z - a), cs);
        }
    }

    // Free placement inside an area: own size, alignment and margin. Extent scales both axes (grows from the anchor).
    private Rect PlaceIn(Rect area)
    {
        var inner = area.Inset(Margin);
        bool autoW = Width.Kind == LengthKind.Auto, autoH = Height.Kind == LengthKind.Auto;
        var m = autoW || autoH ? Measure(inner.Width, inner.Height) : default;
        float e = Extent;
        int w = LengthSolver.Round(Math.Min(inner.Width, Width.ResolveAlone(inner.Width, m.Width)) * e);
        int h = LengthSolver.Round(Math.Min(inner.Height, Height.ResolveAlone(inner.Height, m.Height)) * e);
        return inner.Place(w, h, HorizontalAlign, VerticalAlign);
    }

    #endregion
}
