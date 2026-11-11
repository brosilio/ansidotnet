using System;
using System.Collections.Generic;
using System.Text;

namespace AnsiDotNet;

/// <summary>Character encoding used on the wire.</summary>
public enum TextEncoding : byte
{
    Utf8,
    /// <summary>IBM PC code page 437, as used by classic BBS clients (SyncTERM, NetRunner, etc).</summary>
    Cp437,
    /// <summary>7-bit ASCII; anything else becomes '?'.</summary>
    Ascii,
}

/// <summary>Code page 437 conversion. Self-contained so no encoding provider is needed.</summary>
public static class Cp437
{
    private const string High =
        "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»" +
        "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    // Glyphs shown for 0x00-0x1F and 0x7F when rendering art, not used when transmitting.
    private const string Low = " ☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼";

    private static Dictionary<int, byte>? _encode;

    /// <summary>Decodes a single byte. With <paramref name="controlGlyphs"/>, control bytes map to their CP437 pictures.</summary>
    public static char Decode(byte b, bool controlGlyphs = false)
    {
        if (b >= 0x80)
            return High[b - 0x80];
        if (controlGlyphs)
        {
            if (b < 0x20)
                return Low[b];
            if (b == 0x7F)
                return '⌂';
        }
        return (char)b;
    }

    public static string Decode(ReadOnlySpan<byte> bytes, bool controlGlyphs = false)
    {
        Span<char> buf = bytes.Length <= 512 ? stackalloc char[bytes.Length] : new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            buf[i] = Decode(bytes[i], controlGlyphs);
        return new string(buf);
    }

    /// <summary>Encodes a scalar value, returning false when it has no CP437 equivalent.</summary>
    public static bool TryEncode(Rune rune, out byte value)
    {
        int v = rune.Value;
        if (v < 0x80)
        {
            value = (byte)v;
            return true;
        }
        return (_encode ??= BuildTable()).TryGetValue(v, out value);
    }

    public static byte Encode(Rune rune, byte fallback = (byte)'?') => TryEncode(rune, out var b) ? b : fallback;

    private static Dictionary<int, byte> BuildTable()
    {
        var d = new Dictionary<int, byte>(256);
        for (int i = 0; i < High.Length; i++)
            d[High[i]] = (byte)(0x80 + i);

        // Close substitutes for common characters outside the code page.
        ReadOnlySpan<(char From, char To)> subs =
        [
            ('╭', '┌'), ('╮', '┐'), ('╰', '└'), ('╯', '┘'),
            ('━', '═'), ('┃', '║'), ('┏', '╔'), ('┓', '╗'), ('┗', '╚'), ('┛', '╝'),
            ('┣', '╠'), ('┫', '╣'), ('┳', '╦'), ('┻', '╩'), ('╋', '╬'),
            ('•', '∙'), ('●', '■'), ('◆', '■'), ('▪', '■'), ('…', '.'),
            ('→', '>'), ('←', '<'), ('↑', '^'), ('↓', 'v'), ('›', '>'), ('‹', '<'),
            ('▶', '>'), ('►', '>'), ('◀', '<'), ('◄', '<'), ('▲', '^'), ('▼', 'v'),
            ('‘', '\''), ('’', '\''), ('“', '"'), ('”', '"'), ('–', '-'), ('—', '-'),
            ('✓', '√'), ('×', 'x'),
        ];
        foreach (var (from, to) in subs)
        {
            if (d.ContainsKey(from))
                continue;
            if (to < 0x80)
                d[from] = (byte)to;
            else if (d.TryGetValue(to, out var b))
                d[from] = b;
        }
        return d;
    }
}
