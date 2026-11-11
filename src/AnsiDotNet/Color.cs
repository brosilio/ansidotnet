using System;
using System.Globalization;

namespace AnsiDotNet;

/// <summary>How many colors the output target can display. Higher modes are downsampled on write.</summary>
public enum ColorMode : byte
{
    None,
    Basic16,
    Indexed256,
    TrueColor,
}

public enum ColorKind : byte
{
    Default,
    Indexed,
    Rgb,
}

/// <summary>A terminal color: the terminal default, a palette index (0-255), or 24-bit RGB. Four bytes, blittable.</summary>
public readonly struct Color : IEquatable<Color>
{
    private readonly uint _value; // [kind:8][payload:24]

    private Color(uint value) => _value = value;

    public static Color Default => default;

    public static Color Black => Indexed(0);
    public static Color Red => Indexed(1);
    public static Color Green => Indexed(2);
    public static Color Yellow => Indexed(3);
    public static Color Blue => Indexed(4);
    public static Color Magenta => Indexed(5);
    public static Color Cyan => Indexed(6);
    public static Color White => Indexed(7);
    public static Color BrightBlack => Indexed(8);
    public static Color BrightRed => Indexed(9);
    public static Color BrightGreen => Indexed(10);
    public static Color BrightYellow => Indexed(11);
    public static Color BrightBlue => Indexed(12);
    public static Color BrightMagenta => Indexed(13);
    public static Color BrightCyan => Indexed(14);
    public static Color BrightWhite => Indexed(15);
    public static Color Gray => BrightBlack;

    public static Color Indexed(byte index) => new(((uint)ColorKind.Indexed << 24) | index);

    public static Color Rgb(byte r, byte g, byte b) => new(((uint)ColorKind.Rgb << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    /// <summary>From a packed 0xRRGGBB value.</summary>
    public static Color FromHex(uint rgb) => new(((uint)ColorKind.Rgb << 24) | (rgb & 0xFFFFFF));

    /// <summary>Linear interpolation in RGB space; non-RGB inputs are resolved through the xterm palette.</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        var (ar, ag, ab) = a.ToRgb();
        var (br, bg, bb) = b.ToRgb();
        t = Math.Clamp(t, 0f, 1f);
        return Rgb(
            (byte)(ar + (br - ar) * t),
            (byte)(ag + (bg - ag) * t),
            (byte)(ab + (bb - ab) * t));
    }

    /// <summary>Hue in degrees, saturation and value in 0..1.</summary>
    public static Color FromHsv(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = v * s, x = c * (1 - MathF.Abs(h / 60f % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60f) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Rgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Parses "#rgb", "#rrggbb" (hash optional) or a palette index "0".."255".</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out Color color)
    {
        color = default;
        s = s.Trim();
        if (s.IsEmpty)
            return false;
        bool hash = s[0] == '#';
        if (hash)
            s = s[1..];
        if (!hash && s.Length <= 3 && byte.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
        {
            color = Indexed(idx);
            return true;
        }
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        if (s.Length == 6)
        {
            color = FromHex(v);
            return true;
        }
        if (s.Length == 3)
        {
            uint r = (v >> 8) & 0xF, g = (v >> 4) & 0xF, b = v & 0xF;
            color = Rgb((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            return true;
        }
        return false;
    }

    public ColorKind Kind => (ColorKind)(_value >> 24);
    public bool IsDefault => _value == 0;
    public byte Index => (byte)_value;
    public byte R => (byte)(_value >> 16);
    public byte G => (byte)(_value >> 8);
    public byte B => (byte)_value;

    /// <summary>Resolves to RGB. Default maps to black for lack of a better answer.</summary>
    public (byte R, byte G, byte B) ToRgb() => Kind switch
    {
        ColorKind.Rgb => (R, G, B),
        ColorKind.Indexed => Palette.IndexToRgb(Index),
        _ => (0, 0, 0),
    };

    /// <summary>Returns the closest color representable in <paramref name="mode"/>.</summary>
    public Color Downsample(ColorMode mode)
    {
        switch (mode)
        {
            case ColorMode.TrueColor:
                return this;
            case ColorMode.None:
                return Default;
            case ColorMode.Indexed256:
                return Kind == ColorKind.Rgb ? Indexed(Palette.RgbTo256(R, G, B)) : this;
            default:
                return Kind switch
                {
                    ColorKind.Rgb => Indexed(Palette.RgbTo16(R, G, B)),
                    ColorKind.Indexed when Index >= 16 => Indexed(Palette.IndexTo16(Index)),
                    _ => this,
                };
        }
    }

    public bool Equals(Color other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => (int)_value;
    public static bool operator ==(Color a, Color b) => a._value == b._value;
    public static bool operator !=(Color a, Color b) => a._value != b._value;

    public override string ToString() => Kind switch
    {
        ColorKind.Rgb => $"#{R:x2}{G:x2}{B:x2}",
        ColorKind.Indexed => Index.ToString(CultureInfo.InvariantCulture),
        _ => "default",
    };

    public static implicit operator Color(ConsoleColor c) => Indexed(c switch
    {
        ConsoleColor.Black => 0,
        ConsoleColor.DarkRed => 1,
        ConsoleColor.DarkGreen => 2,
        ConsoleColor.DarkYellow => 3,
        ConsoleColor.DarkBlue => 4,
        ConsoleColor.DarkMagenta => 5,
        ConsoleColor.DarkCyan => 6,
        ConsoleColor.Gray => 7,
        ConsoleColor.DarkGray => 8,
        ConsoleColor.Red => 9,
        ConsoleColor.Green => 10,
        ConsoleColor.Yellow => 11,
        ConsoleColor.Blue => 12,
        ConsoleColor.Magenta => 13,
        ConsoleColor.Cyan => 14,
        _ => 15,
    });
}

internal static class Palette
{
    // xterm defaults
    private static readonly byte[] Base16 =
    [
        0, 0, 0, 205, 0, 0, 0, 205, 0, 205, 205, 0, 0, 0, 238, 205, 0, 205, 0, 205, 205, 229, 229, 229,
        127, 127, 127, 255, 0, 0, 0, 255, 0, 255, 255, 0, 92, 92, 255, 255, 0, 255, 0, 255, 255, 255, 255, 255,
    ];

    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    private static byte[]? _indexTo16;

    public static (byte, byte, byte) IndexToRgb(byte i)
    {
        if (i < 16)
            return (Base16[i * 3], Base16[i * 3 + 1], Base16[i * 3 + 2]);
        if (i < 232)
        {
            int n = i - 16;
            return (CubeLevels[n / 36], CubeLevels[n / 6 % 6], CubeLevels[n % 6]);
        }
        byte g = (byte)(8 + (i - 232) * 10);
        return (g, g, g);
    }

    public static byte RgbTo256(byte r, byte g, byte b)
    {
        int ri = CubeIndex(r), gi = CubeIndex(g), bi = CubeIndex(b);
        int cubeDist = Distance(r, g, b, CubeLevels[ri], CubeLevels[gi], CubeLevels[bi]);

        int avg = (r + g + b) / 3;
        int grayIdx = avg < 8 ? 0 : Math.Min(23, (avg - 8 + 5) / 10);
        int gv = 8 + grayIdx * 10;
        int grayDist = Distance(r, g, b, gv, gv, gv);

        return grayDist < cubeDist ? (byte)(232 + grayIdx) : (byte)(16 + ri * 36 + gi * 6 + bi);
    }

    public static byte RgbTo16(byte r, byte g, byte b)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            int d = Distance(r, g, b, Base16[i * 3], Base16[i * 3 + 1], Base16[i * 3 + 2]);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return (byte)best;
    }

    public static byte IndexTo16(byte i)
    {
        var table = _indexTo16;
        if (table is null)
        {
            table = new byte[256];
            for (int n = 0; n < 256; n++)
            {
                var (r, g, b) = IndexToRgb((byte)n);
                table[n] = n < 16 ? (byte)n : RgbTo16(r, g, b);
            }
            _indexTo16 = table;
        }
        return table[i];
    }

    private static int CubeIndex(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;

    // Weighted euclidean; cheap and perceptually better than plain RGB distance.
    private static int Distance(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        int dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }
}
