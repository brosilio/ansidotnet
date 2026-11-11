using System;
using System.Text;

namespace AnsiDotNet;

[Flags]
public enum TextAttributes : uint
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,
    DoubleUnderline = 1 << 8,
    Overline = 1 << 9,
}

/// <summary>Foreground, background and attributes. Twelve bytes with no padding, so cells compare bitwise.</summary>
public readonly struct Style : IEquatable<Style>
{
    public Color Foreground { get; init; }
    public Color Background { get; init; }
    public TextAttributes Attributes { get; init; }

    public Style(Color foreground, Color background = default, TextAttributes attributes = TextAttributes.None)
    {
        Foreground = foreground;
        Background = background;
        Attributes = attributes;
    }

    public static Style Default => default;

    public Style WithForeground(Color c) => this with { Foreground = c };
    public Style WithBackground(Color c) => this with { Background = c };
    public Style WithAttributes(TextAttributes a) => this with { Attributes = a };
    public Style Add(TextAttributes a) => this with { Attributes = Attributes | a };
    public Style Remove(TextAttributes a) => this with { Attributes = Attributes & ~a };
    public bool Has(TextAttributes a) => (Attributes & a) == a;

    public bool Equals(Style other) =>
        Foreground == other.Foreground && Background == other.Background && Attributes == other.Attributes;

    public override bool Equals(object? obj) => obj is Style s && Equals(s);
    public override int GetHashCode() => HashCode.Combine(Foreground, Background, Attributes);
    public static bool operator ==(Style a, Style b) => a.Equals(b);
    public static bool operator !=(Style a, Style b) => !a.Equals(b);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("fg=").Append(Foreground).Append(" bg=").Append(Background);
        if (Attributes != TextAttributes.None)
            sb.Append(' ').Append(Attributes);
        return sb.ToString();
    }

    public static implicit operator Style(Color foreground) => new(foreground);
}
