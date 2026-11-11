using System;

namespace AnsiDotNet.Layout;

/// <summary>
/// Shares a line of cells between items described by <see cref="Length"/>s. Works on plain numbers, so it is useful
/// outside the node tree too (game HUDs, custom grids).
/// </summary>
public static class LengthSolver
{
    /// <summary>
    /// Computes item sizes. Fixed, fraction and auto items get their size first, then fill items share what is left
    /// by weight. If there isn't enough room, fill items shrink to their minimum first, then fraction/auto items, then
    /// fixed ones. <paramref name="measured"/> supplies content sizes for Auto items and <paramref name="scale"/>
    /// multiplies each item (used for expand/collapse animation); either may be empty. Fractions are of
    /// <paramref name="fractionOf"/> when given (e.g. the whole container, before gaps), otherwise of <paramref name="available"/>.
    /// </summary>
    public static void Solve(float available, ReadOnlySpan<Length> lengths, ReadOnlySpan<int> measured, ReadOnlySpan<float> scale, Span<float> sizes, float fractionOf = -1)
    {
        int n = lengths.Length;
        if (sizes.Length < n)
            throw new ArgumentException("sizes is shorter than lengths", nameof(sizes));
        available = Math.Max(0, available);
        float basis = fractionOf >= 0 ? fractionOf : available;

        float used = 0;
        for (int i = 0; i < n; i++)
        {
            var l = lengths[i];
            float s = Scale(scale, i);
            sizes[i] = l.Resolve(basis, i < measured.Length ? measured[i] : 0) * s;
            used += sizes[i];
        }

        float remaining = available - used;
        if (remaining > 0.0001f)
            Grow(lengths, scale, sizes, remaining);
        else if (remaining < -0.0001f)
            Shrink(lengths, scale, sizes, -remaining);
    }

    private static float Scale(ReadOnlySpan<float> scale, int i) => i < scale.Length ? Math.Clamp(scale[i], 0f, 1f) : 1f;

    private static void Grow(ReadOnlySpan<Length> lengths, ReadOnlySpan<float> scale, Span<float> sizes, float remaining)
    {
        int n = lengths.Length;
        Span<bool> frozen = n <= 256 ? stackalloc bool[n] : new bool[n];
        frozen.Clear();
        // Repeat until no fill item hits its maximum; each pass freezes at least one item or finishes.
        while (remaining > 0.0001f)
        {
            float weights = 0;
            for (int i = 0; i < n; i++)
            {
                if (!frozen[i] && lengths[i].Kind == LengthKind.Fill)
                    weights += lengths[i].Value * Scale(scale, i);
            }
            if (weights <= 0)
                return;

            bool capped = false;
            for (int i = 0; i < n; i++)
            {
                var l = lengths[i];
                if (frozen[i] || l.Kind != LengthKind.Fill)
                    continue;
                float s = Scale(scale, i);
                float share = remaining * l.Value * s / weights;
                float room = l.Max == int.MaxValue ? float.MaxValue : l.Max * s - sizes[i];
                if (share >= room)
                {
                    sizes[i] += Math.Max(0, room);
                    remaining -= Math.Max(0, room);
                    frozen[i] = true;
                    capped = true;
                }
            }
            if (capped)
                continue;
            for (int i = 0; i < n; i++)
            {
                var l = lengths[i];
                if (!frozen[i] && l.Kind == LengthKind.Fill)
                    sizes[i] += remaining * l.Value * Scale(scale, i) / weights;
            }
            return;
        }
    }

    private static void Shrink(ReadOnlySpan<Length> lengths, ReadOnlySpan<float> scale, Span<float> sizes, float deficit)
    {
        for (int tier = 0; tier < 3 && deficit > 0.0001f; tier++)
        {
            float shrinkable = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (Tier(lengths[i].Kind) == tier)
                    shrinkable += Math.Max(0, sizes[i] - lengths[i].Min * Scale(scale, i));
            }
            if (shrinkable <= 0)
                continue;
            float k = Math.Min(1f, deficit / shrinkable);
            for (int i = 0; i < lengths.Length; i++)
            {
                if (Tier(lengths[i].Kind) == tier)
                    sizes[i] -= Math.Max(0, sizes[i] - lengths[i].Min * Scale(scale, i)) * k;
            }
            deficit -= shrinkable * k;
        }
        // Whatever is still too big overflows; the caller clips.
    }

    private static int Tier(LengthKind k) => k switch
    {
        LengthKind.Fill => 0,
        LengthKind.Fixed => 2,
        _ => 1,
    };

    /// <summary>
    /// Solves and positions items along a line starting at <paramref name="start"/>. <paramref name="before"/> and
    /// <paramref name="after"/> are fixed spaces around each item (gaps, margins) and may be empty. Edges are rounded
    /// from exact positions, so neighbors never overlap or leave holes and animated sizes move smoothly.
    /// </summary>
    public static void Arrange(int start, int available, ReadOnlySpan<Length> lengths, ReadOnlySpan<int> measured, ReadOnlySpan<float> scale,
        ReadOnlySpan<float> before, ReadOnlySpan<float> after, Justify justify, Span<int> positions, Span<int> sizes)
    {
        int n = lengths.Length;
        if (n == 0)
            return;
        Span<float> exact = n <= 128 ? stackalloc float[n] : new float[n];

        float overhead = 0;
        for (int i = 0; i < n; i++)
            overhead += (i < before.Length ? before[i] : 0) + (i < after.Length ? after[i] : 0);
        Solve(available - overhead, lengths, measured, scale, exact, available);

        float total = overhead;
        for (int i = 0; i < n; i++)
            total += exact[i];
        float leftover = Math.Max(0, available - total);

        float offset = 0, extra = 0;
        switch (justify)
        {
            case Justify.Center: offset = leftover / 2; break;
            case Justify.End: offset = leftover; break;
            case Justify.SpaceBetween when n > 1: extra = leftover / (n - 1); break;
            case Justify.SpaceAround: extra = leftover / n; offset = extra / 2; break;
            case Justify.SpaceEvenly: extra = leftover / (n + 1); offset = extra; break;
        }

        float cursor = start + offset;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                cursor += extra;
            cursor += i < before.Length ? before[i] : 0;
            int a = Round(cursor);
            cursor += exact[i];
            positions[i] = a;
            sizes[i] = Round(cursor) - a;
            cursor += i < after.Length ? after[i] : 0;
        }
    }

    internal static int Round(float v) => (int)MathF.Floor(v + 0.5f);
}
