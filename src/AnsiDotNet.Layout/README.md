# AnsiDotNet.Layout

Layout, text layout and animation for cell grids, built on [AnsiDotNet](../../README.md). Zero dependencies beyond it, AOT- and trim-safe, and allocation-free per frame in steady state. It suits TUIs, games, BBS doors and anything else that draws into a `Surface`.

It has three layers. Use whichever you need:

| Layer | Types | For |
|---|---|---|
| Math | `Length`, `LengthSolver`, `Thickness`, `RectExtensions` | Splitting rectangles by hand, immediate-mode style |
| Text | `TextLayout`, `LineBreaker`, `TextBlock`, `RichText` | Wrapping, alignment, justify, ellipsis, scrolling, styled runs |
| Tree | `Node`, `TextNode`, `Stage`, `Tween`, `Ease` | Retained layouts with docking, stacking, layers and animation |

## Immediate mode

```csharp
var (header, body, footer) = screen.Bounds.SplitRows(1, Length.Fill(), 1);

Span<Rect> cols = stackalloc Rect[2];
body.Split(Axis.Horizontal, [Length.Fill(), Length.Percent(30).AtLeast(20)], cols, gap: 1);

var dialog = body.Place(40, 10);                     // centered
var sidebar = body.Take(Edge.Right, 24, out var rest);
```

`Length` comes in four kinds:
- **Fixed:** cells. Plain `int`s convert to it.
- **Fraction:** `Percent`, `Ratio`, a share of the container.
- **Fill:** a weighted share of what's left.
- **Auto:** the content size.

Any length can be clamped with `AtLeast` and `AtMost`. When space runs short, fills shrink first, then fractions and autos, then fixed sizes. Edges are rounded from exact positions, so neighbors never overlap or leave gaps.

## Text

```csharp
var text = new TextBlock("Long text that wraps at word boundaries…")
{
    Align = TextAlign.Justify, Ellipsis = true, ScrollY = 0,
};
text.Draw(surface, area);

foreach (var line in TextLayout.Lines(span, width))   // lazy, no allocation
    Console.WriteLine(span.Slice(line.Start, line.Length));
```

The text layout handles:
- **Wrapping:** wraps at spaces, after hyphens and between CJK characters, and splits words only when they can't fit on a line.
- **Widths:** wide characters take two cells, tabs expand to tab stops, and newlines are respected.
- **Caching:** `TextBlock` caches wrapped lines per width, so redrawing an unchanged block costs only the drawing.
- **Styled runs:** `RichText` builds styled runs and reuses its buffers. Default colors in a run inherit from the block's style.

## The node tree

```csharp
var stage = new Stage(new Node { Layout = LayoutKind.Layer });

var sidebar = new Node { Dock = Edge.Left, Width = 24, Border = BoxStyle.Rounded, Title = "Files" };
var log = new TextNode(richText) { FollowTail = true, Border = BoxStyle.Rounded };
var app = new Node { Layout = LayoutKind.Dock }.Add(
    new TextNode("My app") { Dock = Edge.Top, Height = 1, Background = barStyle },
    sidebar,
    log);
var toast = new TextNode("Saved") { Width = 30, Height = Length.Auto, HorizontalAlign = Align.End, VerticalAlign = Align.End, Visible = false };
stage.Root.Add(app, toast);

// each frame
stage.Render(surface, elapsed);        // Update + Layout + Draw
terminal.Render(surface);
```

Nodes arrange their children as a **Stack** (row or column, solved by `Length`, with `Gap` and `Justify`), a **Dock** (children take edges in order, and the rest fill), or a **Layer** (children overlap, placed by size and alignment). Every node has `Margin`, `Padding`, an optional `Background`, `Border` and `Title`, and draws its content through `Content` (a delegate) or by overriding `OnRender`. Measuring for `Length.Auto` uses `Measurer` or `MeasureContent`. `Stage.HitTest(x, y)` returns the topmost node at a point.

Content callbacks receive the surface and the area to draw in. Always draw relative to that area: a node that is fading is drawn into an off-screen surface first, so the area isn't always its screen position.

## Animation

```csharp
toast.Show(Effect.Fade | Effect.SlideRight, ease: Ease.OutBack, completed: () =>
    toast.Hide(Effect.Fade | Effect.Expand, delay: TimeSpan.FromSeconds(3)));

sidebar.Toggle(Effect.Expand | Effect.SlideLeft);         // a drawer; the log reflows as it moves
sidebar.Undock(floatingLayer, 40, 20);                    // flies out of the dock into a floating layer
sidebar.DockTo(app, Edge.Left, 24);                       // and back
highlight.LayoutTransition = TimeSpan.FromMilliseconds(150); // any layout change glides
stage.AnimateNextLayout(); reorderThings();               // everything that moves glides from where it was
board.Shake();
log.ScrollToEnd();  description.TypeIn(charsPerSecond: 200);
node.Animate(NodeProperty.Opacity, 0.4f, TimeSpan.FromMilliseconds(300), Ease.InOutSine);
```

- **Show/hide effects:** `Fade`, `Expand` (takes space smoothly, pushing siblings), `Slide*` from any edge, `Wipe*` from any edge. Combine them freely. Interrupting a hide with a show reverses it from wherever it is.
- **Fading:** fades are real compositing. The node is drawn over a copy of what's behind it and mixed with `Surface.Blend`, so glyphs cross-fade instead of popping. Set `Stage.DefaultForeground/DefaultBackground` to your theme so default colors blend correctly.
- **Layout transitions:** they animate *where layout puts a node*, so docking, undocking, reordering, resizing a sibling and alignment changes all glide. Children reflow inside their parent's in-between size. A terminal resize snaps instead of animating.
- **Frame pacing:** `Stage.IsAnimating` says whether to keep drawing frames. `Stage.NeedsRender` also covers the update that finished an animation, so the final frame isn't missed.
- **Callbacks:** completion callbacks run after the update pass, so they may add, remove or move nodes.
- **Standalone use:** `Tween` and `Easing.Apply` work on their own for anything else (counters, color pulses with `Color.Lerp`).

## Performance

A 132-node screen at 200×60, with a bordered column layout, a 300-message wrapped chat log and a dialog fading and expanding, takes about 0.2 ms per frame for update, layout and draw, with 0 bytes allocated per frame (NativeAOT, x64).
