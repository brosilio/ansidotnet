using System;

namespace AnsiDotNet;

public readonly record struct TerminalSize(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    /// <summary>Exclusive.</summary>
    public int Right => X + Width;
    /// <summary>Exclusive.</summary>
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public Rect Intersect(Rect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? new Rect(l, t, r - l, b - t) : default;
    }

    /// <summary>Grows (or shrinks, if negative) on every side.</summary>
    public Rect Inflate(int dx, int dy) => new(X - dx, Y - dy, Math.Max(0, Width + 2 * dx), Math.Max(0, Height + 2 * dy));

    public Rect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>A rect of the given size centered within this one.</summary>
    public Rect Center(int width, int height) => new(X + (Width - width) / 2, Y + (Height - height) / 2, width, height);
}
