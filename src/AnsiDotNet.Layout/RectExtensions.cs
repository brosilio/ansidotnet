using System;

namespace AnsiDotNet.Layout;

/// <summary>Immediate-mode layout helpers on <see cref="Rect"/>. None of them allocate.</summary>
public static class RectExtensions
{
    public static Rect Inset(this Rect r, Thickness t) =>
        new(r.X + t.Left, r.Y + t.Top, Math.Max(0, r.Width - t.Horizontal), Math.Max(0, r.Height - t.Vertical));

    public static Rect Outset(this Rect r, Thickness t) =>
        new(r.X - t.Left, r.Y - t.Top, r.Width + t.Horizontal, r.Height + t.Vertical);

    /// <summary>Divides the rect along an axis. <paramref name="result"/> must be at least as long as <paramref name="lengths"/>.</summary>
    public static void Split(this Rect r, Axis axis, ReadOnlySpan<Length> lengths, Span<Rect> result, int gap = 0, Justify justify = Justify.Start)
    {
        int n = lengths.Length;
        if (result.Length < n)
            throw new ArgumentException("result is shorter than lengths", nameof(result));
        if (n == 0)
            return;
        Span<int> pos = n <= 64 ? stackalloc int[n] : new int[n];
        Span<int> size = n <= 64 ? stackalloc int[n] : new int[n];
        Span<float> before = n <= 64 ? stackalloc float[n] : new float[n];
        before.Fill(gap);
        before[0] = 0;
        bool h = axis == Axis.Horizontal;
        LengthSolver.Arrange(h ? r.X : r.Y, h ? r.Width : r.Height, lengths, default, default, before, default, justify, pos, size);
        for (int i = 0; i < n; i++)
            result[i] = h ? new Rect(pos[i], r.Y, size[i], r.Height) : new Rect(r.X, pos[i], r.Width, size[i]);
    }

    // The tuple helpers take no gap: with int converting to Length, an optional gap would silently swallow a size.
    public static (Rect, Rect) SplitRows(this Rect r, Length a, Length b)
    {
        Span<Rect> o = stackalloc Rect[2];
        r.Split(Axis.Vertical, [a, b], o);
        return (o[0], o[1]);
    }

    public static (Rect, Rect, Rect) SplitRows(this Rect r, Length a, Length b, Length c)
    {
        Span<Rect> o = stackalloc Rect[3];
        r.Split(Axis.Vertical, [a, b, c], o);
        return (o[0], o[1], o[2]);
    }

    public static (Rect, Rect, Rect, Rect) SplitRows(this Rect r, Length a, Length b, Length c, Length d)
    {
        Span<Rect> o = stackalloc Rect[4];
        r.Split(Axis.Vertical, [a, b, c, d], o);
        return (o[0], o[1], o[2], o[3]);
    }

    public static (Rect, Rect) SplitColumns(this Rect r, Length a, Length b)
    {
        Span<Rect> o = stackalloc Rect[2];
        r.Split(Axis.Horizontal, [a, b], o);
        return (o[0], o[1]);
    }

    public static (Rect, Rect, Rect) SplitColumns(this Rect r, Length a, Length b, Length c)
    {
        Span<Rect> o = stackalloc Rect[3];
        r.Split(Axis.Horizontal, [a, b, c], o);
        return (o[0], o[1], o[2]);
    }

    public static (Rect, Rect, Rect, Rect) SplitColumns(this Rect r, Length a, Length b, Length c, Length d)
    {
        Span<Rect> o = stackalloc Rect[4];
        r.Split(Axis.Horizontal, [a, b, c, d], o);
        return (o[0], o[1], o[2], o[3]);
    }

    /// <summary>Cuts <paramref name="size"/> cells off one edge. Returns the slice; <paramref name="rest"/> gets the remainder.</summary>
    public static Rect Take(this Rect r, Edge edge, int size, out Rect rest)
    {
        bool h = edge is Edge.Left or Edge.Right;
        size = Math.Clamp(size, 0, h ? r.Width : r.Height);
        switch (edge)
        {
            case Edge.Left:
                rest = new Rect(r.X + size, r.Y, r.Width - size, r.Height);
                return r with { Width = size };
            case Edge.Right:
                rest = r with { Width = r.Width - size };
                return new Rect(r.Right - size, r.Y, size, r.Height);
            case Edge.Top:
                rest = new Rect(r.X, r.Y + size, r.Width, r.Height - size);
                return r with { Height = size };
            default:
                rest = r with { Height = r.Height - size };
                return new Rect(r.X, r.Bottom - size, r.Width, size);
        }
    }

    /// <summary>A rect of the given size positioned within this one. Sizes larger than the area are clamped.</summary>
    public static Rect Place(this Rect area, int width, int height, Align horizontal = Align.Center, Align vertical = Align.Center)
    {
        width = Math.Clamp(width, 0, area.Width);
        height = Math.Clamp(height, 0, area.Height);
        return new Rect(area.X + Offset(area.Width - width, horizontal), area.Y + Offset(area.Height - height, vertical), width, height);
    }

    internal static int Offset(int slack, Align align) => align switch
    {
        Align.Center => slack / 2,
        Align.End => slack,
        _ => 0,
    };

    /// <summary>Interpolates each edge separately, so a moving rect also changes size smoothly. Overshoots for t outside 0..1.</summary>
    public static Rect Lerp(this Rect from, Rect to, float t)
    {
        if (t == 0) return from;
        if (t == 1) return to;
        int l = LengthSolver.Round(from.X + (to.X - from.X) * t);
        int top = LengthSolver.Round(from.Y + (to.Y - from.Y) * t);
        int r = LengthSolver.Round(from.Right + (to.Right - from.Right) * t);
        int b = LengthSolver.Round(from.Bottom + (to.Bottom - from.Bottom) * t);
        return new Rect(l, top, Math.Max(0, r - l), Math.Max(0, b - top));
    }
}
