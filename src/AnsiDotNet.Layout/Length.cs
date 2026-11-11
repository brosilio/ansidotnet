using System;

namespace AnsiDotNet.Layout;

public enum LengthKind : byte
{
    /// <summary>A number of cells.</summary>
    Fixed,
    /// <summary>A fraction of the space the parent has.</summary>
    Fraction,
    /// <summary>A share of whatever space is left, by weight.</summary>
    Fill,
    /// <summary>As much as the content asks for.</summary>
    Auto,
}

/// <summary>
/// How big something wants to be along one axis. Converts from <c>int</c> (fixed cells). Clamp with
/// <see cref="AtLeast"/> and <see cref="AtMost"/>.
/// </summary>
public readonly struct Length : IEquatable<Length>
{
    private Length(LengthKind kind, float value, int min = 0, int max = int.MaxValue)
    {
        Kind = kind;
        Value = value;
        Min = min;
        Max = max;
    }

    public LengthKind Kind { get; }

    /// <summary>Cells for Fixed, 0..1 for Fraction, weight for Fill; unused for Auto.</summary>
    public float Value { get; }

    public int Min { get; }
    public int Max { get; }

    public static Length Fixed(int cells) => new(LengthKind.Fixed, Math.Max(0, cells));
    public static Length Percent(float percent) => new(LengthKind.Fraction, Math.Max(0, percent) / 100f);
    public static Length Ratio(int numerator, int denominator) => new(LengthKind.Fraction, denominator <= 0 ? 0 : Math.Max(0f, (float)numerator / denominator));
    public static Length Fill(float weight = 1) => new(LengthKind.Fill, Math.Max(0, weight));
    public static Length Auto => new(LengthKind.Auto, 0);

    public Length AtLeast(int cells) => new(Kind, Value, Math.Max(0, cells), Math.Max(Max, cells));
    public Length AtMost(int cells) => new(Kind, Value, Math.Min(Min, Math.Max(0, cells)), Math.Max(0, cells));

    public static implicit operator Length(int cells) => Fixed(cells);

    /// <summary>The size before any space is shared out (Fill starts at its minimum).</summary>
    public float Resolve(float available, int measured)
    {
        float v = Kind switch
        {
            LengthKind.Fixed => Value,
            LengthKind.Fraction => Value * Math.Max(0, available),
            LengthKind.Auto => measured,
            _ => 0,
        };
        return Math.Clamp(v, Min, Max);
    }

    /// <summary>The size this length takes when it is the only thing in <paramref name="available"/> (Fill takes everything).</summary>
    public int ResolveAlone(int available, int measured)
    {
        float v = Kind == LengthKind.Fill ? available : Resolve(available, measured);
        return (int)Math.Clamp(v, Min, Max);
    }

    public bool Equals(Length o) => Kind == o.Kind && Value == o.Value && Min == o.Min && Max == o.Max;
    public override bool Equals(object? obj) => obj is Length l && Equals(l);
    public override int GetHashCode() => HashCode.Combine(Kind, Value, Min, Max);
    public static bool operator ==(Length a, Length b) => a.Equals(b);
    public static bool operator !=(Length a, Length b) => !a.Equals(b);

    public override string ToString()
    {
        string s = Kind switch
        {
            LengthKind.Fixed => $"{Value}",
            LengthKind.Fraction => $"{Value * 100:0.##}%",
            LengthKind.Fill => Value == 1 ? "fill" : $"fill({Value})",
            _ => "auto",
        };
        if (Min > 0) s += $" ≥{Min}";
        if (Max != int.MaxValue) s += $" ≤{Max}";
        return s;
    }
}
