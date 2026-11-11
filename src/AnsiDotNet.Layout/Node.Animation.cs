using System;

namespace AnsiDotNet.Layout;

public partial class Node
{
    private struct Channel
    {
        public Tween T;
        public Action? Done;
    }

    private const int ShakeChannel = 6, RectChannel = 7;
    private const int TransitionChannels = (1 << (int)NodeProperty.Opacity) | (1 << (int)NodeProperty.Extent)
        | (1 << (int)NodeProperty.Slide) | (1 << (int)NodeProperty.Reveal);

    private readonly Channel[] _ch = CreateChannels();
    private Rect _moveFrom;
    private float _shakeAmplitude;

    private enum TransitionState : byte { None, Showing, Hiding }
    private TransitionState _transition;
    private int _transitionMask;
    private Action? _transitionDone;

    private static Channel[] CreateChannels()
    {
        var c = new Channel[8];
        c[(int)NodeProperty.Opacity].T = Tween.At(1);
        c[(int)NodeProperty.Extent].T = Tween.At(1);
        c[(int)NodeProperty.Slide].T = Tween.At(1);
        c[(int)NodeProperty.Reveal].T = Tween.At(1);
        c[ShakeChannel].T = Tween.At(1);
        c[RectChannel].T = Tween.At(1);
        return c;
    }

    public float Opacity
    {
        get => Math.Clamp(_ch[(int)NodeProperty.Opacity].T.Value, 0, 1);
        set => Set(NodeProperty.Opacity, value);
    }

    public float Extent
    {
        get => Math.Max(0, _ch[(int)NodeProperty.Extent].T.Value);
        set => Set(NodeProperty.Extent, value);
    }

    public float Slide
    {
        get => _ch[(int)NodeProperty.Slide].T.Value;
        set => Set(NodeProperty.Slide, value);
    }

    public float Reveal
    {
        get => _ch[(int)NodeProperty.Reveal].T.Value;
        set => Set(NodeProperty.Reveal, value);
    }

    public float OffsetX
    {
        get => _ch[(int)NodeProperty.OffsetX].T.Value;
        set => Set(NodeProperty.OffsetX, value);
    }

    public float OffsetY
    {
        get => _ch[(int)NodeProperty.OffsetY].T.Value;
        set => Set(NodeProperty.OffsetY, value);
    }

    /// <summary>The edge a partly slid-in node is offset toward.</summary>
    public Edge SlideEdge { get; set; } = Edge.Left;

    /// <summary>The edge a partial reveal starts from.</summary>
    public Edge RevealEdge { get; set; } = Edge.Left;

    /// <summary>When set, any change in where layout puts this node (resize excepted) animates over this long.</summary>
    public TimeSpan? LayoutTransition { get; set; }
    public Ease LayoutEase { get; set; } = Ease.OutCubic;

    /// <summary>Visible and not on its way out.</summary>
    public bool IsShown => Visible && _transition != TransitionState.Hiding;

    /// <summary>True while any of this node's own animations run (not its children's).</summary>
    public bool IsAnimating
    {
        get
        {
            foreach (var c in _ch)
            {
                if (c.T.IsRunning)
                    return true;
            }
            return IsContentAnimating;
        }
    }

    private void Set(NodeProperty p, float value)
    {
        ref var c = ref _ch[(int)p];
        c.T.Set(value);
        c.Done = null;
    }

    /// <summary>
    /// Animates a property from its current value. Starting a new animation on the same property replaces the old one
    /// (its callback is dropped). <paramref name="completed"/> runs after the frame's update, so it may change the tree.
    /// </summary>
    public void Animate(NodeProperty property, float to, TimeSpan? duration = null, Ease? ease = null, TimeSpan delay = default, Action? completed = null)
    {
        ref var c = ref _ch[(int)property];
        c.T.Retarget(to, duration ?? DefaultDuration, ease ?? DefaultEase, delay);
        c.Done = completed;
        if (!c.T.IsRunning)
            Complete(ref c);
    }

    /// <summary>Makes the node visible with the given effects. Interrupting a <see cref="Hide"/> reverses it from where it is.</summary>
    public void Show(Effect effects = Effect.None, TimeSpan? duration = null, Ease? ease = null, TimeSpan delay = default, Action? completed = null)
    {
        if (IsShown && _transition == TransitionState.None)
        {
            completed?.Invoke();
            return;
        }
        bool fromHidden = !Visible;
        int mask = Mask(effects) | (_transition == TransitionState.Hiding ? _transitionMask : 0);
        ApplyEdges(effects);
        Visible = true;
        Run(mask, 1, fromHidden, duration, ease ?? DefaultEase, delay);
        Begin(TransitionState.Showing, mask, completed);
    }

    /// <summary>Hides the node with the given effects; it becomes invisible (and takes no space) when they finish.</summary>
    public void Hide(Effect effects = Effect.None, TimeSpan? duration = null, Ease? ease = null, TimeSpan delay = default, Action? completed = null)
    {
        if (!Visible)
        {
            completed?.Invoke();
            return;
        }
        int mask = Mask(effects);
        if (mask == 0 && delay <= TimeSpan.Zero)
        {
            Visible = false;
            _transition = TransitionState.None;
            Rest(_transitionMask);
            completed?.Invoke();
            return;
        }
        if (mask == 0)
        {
            // No effect but a delay: hold full opacity, then drop out.
            mask = 1 << (int)NodeProperty.Opacity;
            duration = TimeSpan.Zero;
        }
        ApplyEdges(effects);
        Run(mask, 0, false, duration, ease ?? DefaultExitEase, delay);
        Begin(TransitionState.Hiding, mask, completed);
    }

    public void Toggle(Effect effects = Effect.None, TimeSpan? duration = null, Ease? ease = null)
    {
        if (IsShown)
            Hide(effects, duration, ease);
        else
            Show(effects, duration, ease);
    }

    /// <summary>A horizontal shake that decays, e.g. to reject input.</summary>
    public void Shake(float amplitude = 2, TimeSpan? duration = null)
    {
        _shakeAmplitude = amplitude;
        _ch[ShakeChannel].T = new Tween(0, 1, duration ?? TimeSpan.FromMilliseconds(400), Ease.Linear);
    }

    /// <summary>Jumps every running animation to its end (callbacks run on the next update).</summary>
    public void FinishAnimations()
    {
        for (int i = 0; i < _ch.Length; i++)
        {
            if (_ch[i].T.IsRunning)
                _ch[i].T.Finish();
        }
    }

    /// <summary>
    /// Takes this node out of a dock and floats it in <paramref name="layer"/> (a <see cref="LayoutKind.Layer"/> node),
    /// keeping its current size unless given another. The move animates.
    /// </summary>
    public void Undock(Node layer, Length? width = null, Length? height = null, Align horizontal = Align.Center, Align vertical = Align.Center, TimeSpan? duration = null)
    {
        Width = width ?? Length.Fixed(Bounds.Width);
        Height = height ?? Length.Fixed(Bounds.Height);
        HorizontalAlign = horizontal;
        VerticalAlign = vertical;
        Dock = null;
        MoveTo(layer, duration: duration);
    }

    /// <summary>Docks this node to an edge of <paramref name="container"/> (a <see cref="LayoutKind.Dock"/> node). The move animates.</summary>
    public void DockTo(Node container, Edge edge, Length size, int index = 0, TimeSpan? duration = null)
    {
        Dock = edge;
        if (edge is Edge.Left or Edge.Right)
        {
            Width = size;
            Height = Length.Fill();
        }
        else
        {
            Height = size;
            Width = Length.Fill();
        }
        MoveTo(container, index, duration: duration);
    }

    private TimeSpan DefaultDuration => Stage?.DefaultDuration ?? TimeSpan.FromMilliseconds(200);
    private Ease DefaultEase => Stage?.DefaultEase ?? Ease.OutCubic;
    private Ease DefaultExitEase => Stage?.DefaultExitEase ?? Ease.InCubic;

    private static int Mask(Effect e)
    {
        int m = 0;
        if ((e & Effect.Fade) != 0) m |= 1 << (int)NodeProperty.Opacity;
        if ((e & Effect.Expand) != 0) m |= 1 << (int)NodeProperty.Extent;
        if ((e & (Effect.SlideLeft | Effect.SlideTop | Effect.SlideRight | Effect.SlideBottom)) != 0) m |= 1 << (int)NodeProperty.Slide;
        if ((e & (Effect.WipeLeft | Effect.WipeTop | Effect.WipeRight | Effect.WipeBottom)) != 0) m |= 1 << (int)NodeProperty.Reveal;
        return m;
    }

    private void ApplyEdges(Effect e)
    {
        if ((e & Effect.SlideLeft) != 0) SlideEdge = Edge.Left;
        else if ((e & Effect.SlideTop) != 0) SlideEdge = Edge.Top;
        else if ((e & Effect.SlideRight) != 0) SlideEdge = Edge.Right;
        else if ((e & Effect.SlideBottom) != 0) SlideEdge = Edge.Bottom;
        if ((e & Effect.WipeLeft) != 0) RevealEdge = Edge.Left;
        else if ((e & Effect.WipeTop) != 0) RevealEdge = Edge.Top;
        else if ((e & Effect.WipeRight) != 0) RevealEdge = Edge.Right;
        else if ((e & Effect.WipeBottom) != 0) RevealEdge = Edge.Bottom;
    }

    private void Run(int mask, float to, bool fromZero, TimeSpan? duration, Ease ease, TimeSpan delay)
    {
        var d = duration ?? DefaultDuration;
        for (int i = 0; i < 4; i++)
        {
            if ((mask & (1 << i)) == 0)
                continue;
            ref var c = ref _ch[i];
            if (fromZero)
                c.T.Set(0);
            c.T.Retarget(to, d, ease, delay);
            c.Done = null;
        }
    }

    private void Begin(TransitionState state, int mask, Action? completed)
    {
        _transition = state;
        _transitionMask = mask;
        _transitionDone = completed;
    }

    private void Rest(int mask)
    {
        for (int i = 0; i < 4; i++)
        {
            if ((mask & (1 << i)) != 0)
                _ch[i].T.Set(1);
        }
    }

    private void Complete(ref Channel c)
    {
        var done = c.Done;
        c.Done = null;
        if (done is null)
            return;
        if (Stage is { } s)
            s.Defer(done);
        else
            done();
    }

    /// <summary>Queues an action to run after the current update, when changing the tree is safe.</summary>
    protected void Defer(Action action)
    {
        if (Stage is { } s)
            s.Defer(action);
        else
            action();
    }

    /// <summary>Advances animations that subclasses own. Return true while any are running.</summary>
    protected virtual bool UpdateContent(TimeSpan elapsed) => false;

    protected virtual bool IsContentAnimating => false;

    // Returns true if anything in the subtree was animating, including animations that just finished: those still
    // need one more frame drawn.
    internal bool Update(TimeSpan elapsed, Stage stage)
    {
        bool touched = false;
        for (int i = 0; i < _ch.Length; i++)
        {
            ref var c = ref _ch[i];
            if (!c.T.IsRunning)
                continue;
            touched = true;
            bool still = c.T.Update(elapsed);
            if (!still && c.Done is { } done)
            {
                c.Done = null;
                stage.Defer(done);
            }
        }

        if (_transition != TransitionState.None && !AnyRunning(_transitionMask))
        {
            touched = true;
            if (_transition == TransitionState.Hiding)
            {
                Visible = false;
                Rest(_transitionMask);
            }
            _transition = TransitionState.None;
            if (_transitionDone is { } td)
            {
                _transitionDone = null;
                stage.Defer(td);
            }
        }

        touched |= IsContentAnimating;
        UpdateContent(elapsed);
        for (int i = 0; i < _children.Count; i++)
            touched |= _children[i].Update(elapsed, stage);
        return touched;
    }

    private bool AnyRunning(int mask)
    {
        for (int i = 0; i < 4; i++)
        {
            if ((mask & (1 << i)) != 0 && _ch[i].T.IsRunning)
                return true;
        }
        return false;
    }

    internal bool IsAnimatingDeep()
    {
        if (IsAnimating || _transition != TransitionState.None)
            return true;
        foreach (var c in _children)
        {
            if (c.IsAnimatingDeep())
                return true;
        }
        return false;
    }
}
