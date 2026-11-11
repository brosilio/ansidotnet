using System;

namespace AnsiDotNet.Layout;

public enum Axis : byte
{
    Horizontal,
    Vertical,
}

public enum Edge : byte
{
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>Where something smaller than its slot sits within it.</summary>
public enum Align : byte
{
    Start,
    Center,
    End,
}

/// <summary>How a stack distributes space its children don't use.</summary>
public enum Justify : byte
{
    Start,
    Center,
    End,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
}

/// <summary>How a node arranges its children.</summary>
public enum LayoutKind : byte
{
    /// <summary>One after another along <see cref="Node.Direction"/>, sized by the solver.</summary>
    Stack,
    /// <summary>Children with <see cref="Node.Dock"/> take slices off the edges in order; the rest share what is left.</summary>
    Dock,
    /// <summary>Children overlap, each placed by its own size and alignment. Later children draw on top.</summary>
    Layer,
}

/// <summary>
/// Show/hide transitions for <see cref="Node.Show"/> and <see cref="Node.Hide"/>. Combine freely. Slide and wipe
/// effects name the edge the node enters from and leaves through.
/// </summary>
[Flags]
public enum Effect : ushort
{
    None = 0,
    Fade = 1 << 0,
    /// <summary>Grows from (or shrinks to) nothing, pushing siblings aside as it goes.</summary>
    Expand = 1 << 1,
    SlideLeft = 1 << 2,
    SlideTop = 1 << 3,
    SlideRight = 1 << 4,
    SlideBottom = 1 << 5,
    WipeLeft = 1 << 6,
    WipeTop = 1 << 7,
    WipeRight = 1 << 8,
    WipeBottom = 1 << 9,
}

/// <summary>The animatable properties of a <see cref="Node"/>.</summary>
public enum NodeProperty : byte
{
    /// <summary>0..1; mixes the node's pixels with what is behind it.</summary>
    Opacity,
    /// <summary>0..1; scales the space the node takes in its parent, so siblings reflow around it.</summary>
    Extent,
    /// <summary>0..1; how far in from <see cref="Node.SlideEdge"/> the node is (1 = in place).</summary>
    Slide,
    /// <summary>0..1; how much is uncovered, starting from <see cref="Node.RevealEdge"/>.</summary>
    Reveal,
    /// <summary>Cells; a translation applied after layout.</summary>
    OffsetX,
    OffsetY,
}
