using System;
using System.Collections.Generic;

namespace AnsiDotNet.Layout;

/// <summary>
/// Owns a node tree and runs it: <see cref="Update"/> advances animations, <see cref="Layout"/> positions everything,
/// <see cref="Draw"/> paints it onto a surface. <see cref="Render"/> does all three. Not thread-safe; use it from the
/// thread that draws.
/// </summary>
public sealed class Stage
{
    private readonly List<Action> _deferred = new();
    private readonly List<Surface> _pool = new();
    private int _rented;
    private Rect _area;
    private bool _hasArea;
    private bool _animateNext, _changed = true;
    private TimeSpan _animateDuration;
    private Ease _animateEase;

    public Stage() : this(new Node("root")) { }

    public Stage(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Parent is not null || root._stage is not null)
            throw new ArgumentException("The root must not already be in a tree or on another stage.", nameof(root));
        Root = root;
        root._stage = this;
    }

    public Node Root { get; }

    /// <summary>The area passed to the last <see cref="Layout"/>.</summary>
    public Rect Area => _area;

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Easing for animations that bring things in (and for property animations).</summary>
    public Ease DefaultEase { get; set; } = Ease.OutCubic;

    /// <summary>Easing for <see cref="Node.Hide"/>.</summary>
    public Ease DefaultExitEase { get; set; } = Ease.InCubic;

    /// <summary>What default colors count as when fading. Set these to your theme's text and background colors.</summary>
    public Color DefaultForeground { get; set; } = Color.Rgb(204, 204, 204);
    public Color DefaultBackground { get; set; } = Color.Black;

    /// <summary>True while anything on the stage is moving; keep rendering frames until it turns false.</summary>
    public bool IsAnimating => _animateNext || Root.IsAnimatingDeep();

    /// <summary>
    /// True when the next <see cref="Draw"/> would look different because of animation: something is animating, or
    /// moved during an <see cref="Update"/> since the last draw (including the update that finished it).
    /// </summary>
    public bool NeedsRender => _changed || IsAnimating;

    public void Update(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        _changed |= Root.Update(elapsed, this);
        // Callbacks may add, remove or move nodes, so they run after the walk. They can queue more; run those too.
        for (int i = 0; i < _deferred.Count; i++)
            _deferred[i]();
        _deferred.Clear();
    }

    public void Layout(Rect area)
    {
        bool resized = _hasArea && area != _area;
        _area = area;
        _hasArea = true;
        var pass = new Node.Pass(resized, _animateNext, _animateDuration, _animateEase);
        _animateNext = false;
        var rect = area.Inset(Root.Margin);
        Root.ArrangeTarget(rect, pass, false);
        Root.Place(rect, 0, 0, area, area);
    }

    public void Draw(Surface surface)
    {
        var saved = surface.Clip;
        _rented = 0;
        _changed = false;
        Root.Draw(surface, 0, 0, this);
        surface.Clip = saved;
    }

    /// <summary>Update, lay out to the whole surface, and draw.</summary>
    public void Render(Surface surface, TimeSpan elapsed)
    {
        Update(elapsed);
        Layout(surface.Bounds);
        Draw(surface);
    }

    /// <summary>
    /// Animates whatever changes position in the next layout: call it, then add, remove, show, hide, resize or move
    /// nodes, and everything affected glides to its new place instead of jumping.
    /// </summary>
    public void AnimateNextLayout(TimeSpan? duration = null, Ease? ease = null)
    {
        _animateNext = true;
        _animateDuration = duration ?? DefaultDuration;
        _animateEase = ease ?? DefaultEase;
    }

    /// <summary>The topmost visible node at a point, as of the last layout.</summary>
    public Node? HitTest(int x, int y) => Root.HitTest(x, y);

    public Node? Find(string name) => Root.Find(name);

    internal void Defer(Action action) => _deferred.Add(action);

    // Off-screen surfaces for opacity, used in strict nesting order, so a simple stack works.
    internal Surface Rent(int width, int height)
    {
        Surface s;
        if (_rented < _pool.Count)
        {
            s = _pool[_rented];
            s.Resize(width, height);
        }
        else
        {
            s = new Surface(width, height);
            _pool.Add(s);
        }
        _rented++;
        s.ResetClip();
        return s;
    }

    internal void Return() => _rented--;
}
