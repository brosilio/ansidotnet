using System;

namespace AnsiDotNet.Layout;

/// <summary>Space around the four edges of a rectangle (margin, padding). Converts from <c>int</c> (all sides).</summary>
public readonly record struct Thickness(int Left, int Top, int Right, int Bottom)
{
    public Thickness(int all) : this(all, all, all, all) { }
    public Thickness(int horizontal, int vertical) : this(horizontal, vertical, horizontal, vertical) { }

    public int Horizontal => Left + Right;
    public int Vertical => Top + Bottom;
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public static implicit operator Thickness(int all) => new(all);

    public static Thickness operator +(Thickness a, Thickness b) =>
        new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
}
