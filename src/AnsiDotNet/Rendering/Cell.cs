using System;
using System.Text;

namespace AnsiDotNet;

/// <summary>One screen cell: a character and its style. 16 bytes, no padding.</summary>
public readonly struct Cell : IEquatable<Cell>
{
    public Rune Rune { get; }
    public Style Style { get; }

    public Cell(Rune rune, Style style)
    {
        Rune = rune;
        Style = style;
    }

    public Cell(char c, Style style = default) : this(new Rune(c), style) { }

    public static Cell Blank => new(new Rune(' '), default);

    /// <summary>The right half of a wide character. It holds no glyph of its own.</summary>
    public bool IsContinuation => Rune.Value == 0;

    internal static Cell Continuation(Style style) => new(default(Rune), style);

    public bool Equals(Cell other) => Rune == other.Rune && Style.Equals(other.Style);
    public override bool Equals(object? obj) => obj is Cell c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(Rune, Style);
    public static bool operator ==(Cell a, Cell b) => a.Equals(b);
    public static bool operator !=(Cell a, Cell b) => !a.Equals(b);

    public override string ToString() => IsContinuation ? "<cont>" : $"'{Rune}' {Style}";
}
