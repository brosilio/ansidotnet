using System;

namespace AnsiDotNet.Layout;

/// <summary>Easing curves. Back and elastic curves overshoot past 1 before settling.</summary>
public enum Ease : byte
{
    Linear,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InExpo, OutExpo, InOutExpo,
    InSine, OutSine, InOutSine,
    InBack, OutBack, InOutBack,
    OutElastic,
    OutBounce,
    /// <summary>Holds the start value, then jumps to the end.</summary>
    Step,
}

public static class Easing
{
    /// <summary>Maps progress <paramref name="t"/> (0..1) through the curve.</summary>
    public static float Apply(Ease ease, float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const float c1 = 1.70158f, c2 = c1 * 1.525f, c3 = c1 + 1;
        switch (ease)
        {
            case Ease.InQuad: return t * t;
            case Ease.OutQuad: return 1 - (1 - t) * (1 - t);
            case Ease.InOutQuad: return t < 0.5f ? 2 * t * t : 1 - Sq(-2 * t + 2) / 2;
            case Ease.InCubic: return t * t * t;
            case Ease.OutCubic: return 1 - Cube(1 - t);
            case Ease.InOutCubic: return t < 0.5f ? 4 * t * t * t : 1 - Cube(-2 * t + 2) / 2;
            case Ease.InQuart: return Sq(t * t);
            case Ease.OutQuart: return 1 - Sq(Sq(1 - t));
            case Ease.InOutQuart: return t < 0.5f ? 8 * Sq(t * t) : 1 - Sq(Sq(-2 * t + 2)) / 2;
            case Ease.InExpo: return MathF.Pow(2, 10 * t - 10);
            case Ease.OutExpo: return 1 - MathF.Pow(2, -10 * t);
            case Ease.InOutExpo: return t < 0.5f ? MathF.Pow(2, 20 * t - 10) / 2 : (2 - MathF.Pow(2, -20 * t + 10)) / 2;
            case Ease.InSine: return 1 - MathF.Cos(t * MathF.PI / 2);
            case Ease.OutSine: return MathF.Sin(t * MathF.PI / 2);
            case Ease.InOutSine: return -(MathF.Cos(MathF.PI * t) - 1) / 2;
            case Ease.InBack: return c3 * t * t * t - c1 * t * t;
            case Ease.OutBack: return 1 + c3 * Cube(t - 1) + c1 * Sq(t - 1);
            case Ease.InOutBack:
                return t < 0.5f
                    ? Sq(2 * t) * ((c2 + 1) * 2 * t - c2) / 2
                    : (Sq(2 * t - 2) * ((c2 + 1) * (t * 2 - 2) + c2) + 2) / 2;
            case Ease.OutElastic: return MathF.Pow(2, -10 * t) * MathF.Sin((t * 10 - 0.75f) * (2 * MathF.PI / 3)) + 1;
            case Ease.OutBounce: return Bounce(t);
            case Ease.Step: return 0;
            default: return t;
        }
    }

    private static float Sq(float x) => x * x;
    private static float Cube(float x) => x * x * x;

    private static float Bounce(float t)
    {
        const float n = 7.5625f, d = 2.75f;
        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) { t -= 1.5f / d; return n * t * t + 0.75f; }
        if (t < 2.5f / d) { t -= 2.25f / d; return n * t * t + 0.9375f; }
        t -= 2.625f / d;
        return n * t * t + 0.984375f;
    }
}
