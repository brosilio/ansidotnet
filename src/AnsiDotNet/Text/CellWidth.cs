using System;
using System.Globalization;
using System.Text;

namespace AnsiDotNet;

/// <summary>Terminal column width of text (a compact wcwidth).</summary>
public static class CellWidth
{
    // Inclusive ranges of East Asian Wide/Fullwidth and emoji presentation code points.
    private static readonly int[] Wide =
    [
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
        0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
        0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
        0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
        0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
        0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x2E80, 0x303E,
        0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F, 0xAC00, 0xD7A3,
        0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6, 0x16FE0, 0x16FE4,
        0x17000, 0x18CFF, 0x1B000, 0x1B2FF, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E,
        0x1F191, 0x1F19A, 0x1F200, 0x1F251, 0x1F300, 0x1F320, 0x1F32D, 0x1F335, 0x1F337, 0x1F37C,
        0x1F37E, 0x1F393, 0x1F3A0, 0x1F3CA, 0x1F3CF, 0x1F3D3, 0x1F3E0, 0x1F3F0, 0x1F3F4, 0x1F3F4,
        0x1F3F8, 0x1F43E, 0x1F440, 0x1F440, 0x1F442, 0x1F4FC, 0x1F4FF, 0x1F53D, 0x1F54B, 0x1F54E,
        0x1F550, 0x1F567, 0x1F57A, 0x1F57A, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A4, 0x1F5FB, 0x1F64F,
        0x1F680, 0x1F6C5, 0x1F6CC, 0x1F6CC, 0x1F6D0, 0x1F6D2, 0x1F6D5, 0x1F6D7, 0x1F6DC, 0x1F6DF,
        0x1F6EB, 0x1F6EC, 0x1F6F4, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0, 0x1F90C, 0x1F93A,
        0x1F93C, 0x1F945, 0x1F947, 0x1F9FF, 0x1FA70, 0x1FAFF, 0x20000, 0x2FFFD, 0x30000, 0x3FFFD,
    ];

    /// <summary>0 for combining/format/control characters, 2 for wide characters, otherwise 1.</summary>
    public static int Of(Rune rune)
    {
        int v = rune.Value;
        if (v < 0x7F)
            return v >= 0x20 ? 1 : 0;
        if (v < 0xA0)
            return 0;
        if (v < 0x300)
            return 1;

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
            case UnicodeCategory.Control:
                return 0;
        }
        if (v >= 0x1160 && v <= 0x11FF)
            return 0; // Hangul medial vowels / final consonants combine

        return IsWide(v) ? 2 : 1;
    }

    public static int Of(char c) => Rune.TryCreate(c, out var r) ? Of(r) : 1;

    /// <summary>Total width of a string. Escape sequences are not interpreted; see <see cref="AnsiText.VisibleWidth"/>.</summary>
    public static int Of(ReadOnlySpan<char> text)
    {
        int w = 0;
        foreach (var r in text.EnumerateRunes())
            w += Of(r);
        return w;
    }

    /// <summary>Length (in chars) of the longest prefix of <paramref name="text"/> fitting in <paramref name="columns"/>.</summary>
    public static int Fit(ReadOnlySpan<char> text, int columns, out int width)
    {
        int i = 0;
        width = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[i..], out var r, out int n) != System.Buffers.OperationStatus.Done)
                r = Rune.ReplacementChar;
            int cw = Of(r);
            if (width + cw > columns)
                break;
            width += cw;
            i += n;
        }
        return i;
    }

    private static bool IsWide(int v)
    {
        var t = Wide;
        if (v < t[0] || v > t[^1])
            return false;
        int lo = 0, hi = t.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (v < t[mid * 2])
                hi = mid - 1;
            else if (v > t[mid * 2 + 1])
                lo = mid + 1;
            else
                return true;
        }
        return false;
    }
}
