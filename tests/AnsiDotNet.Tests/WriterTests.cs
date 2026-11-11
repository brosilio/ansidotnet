using System.Text;

namespace AnsiDotNet.Tests;

public class WriterTests
{
    private static string Render(Action<AnsiWriter> act, ColorMode mode = ColorMode.TrueColor, TextEncoding enc = TextEncoding.Utf8)
    {
        var w = new AnsiWriter { ColorMode = mode, Encoding = enc };
        act(w);
        return enc == TextEncoding.Utf8 ? Encoding.UTF8.GetString(w.WrittenSpan) : Encoding.Latin1.GetString(w.WrittenSpan);
    }

    [Fact]
    public void TrueColorPrecisionRoundsAndMergesNearbyColors()
    {
        Assert.Equal("\e[38;2;197;99;8m", Render(w =>
        {
            w.TrueColorPrecision = 5;
            w.SetForeground(Color.Rgb(200, 100, 6));
        }));
        Assert.Equal("\e[38;2;255;255;255m", Render(w =>
        {
            w.TrueColorPrecision = 5;
            w.SetForeground(Color.Rgb(255, 255, 255));
        }));
        // 200 and 201 land on the same level, so only the background changes.
        Assert.Equal("\e[48;2;0;0;0m", Render(w =>
        {
            w.TrueColorPrecision = 5;
            w.TransitionStyle(new Style(Color.Rgb(200, 100, 6), Color.White), new Style(Color.Rgb(201, 100, 6), Color.Rgb(1, 2, 3)));
        }));
        Assert.Equal("\e[91m", Render(w =>
        {
            w.TrueColorPrecision = 1;
            w.SetForeground(Color.BrightRed); // palette colors are untouched
        }));
    }

    [Fact]
    public void CursorMovementIsOneBased()
    {
        Assert.Equal("\e[1;1H", Render(w => w.MoveTo(0, 0)));
        Assert.Equal("\e[12;80H", Render(w => w.MoveTo(79, 11)));
        Assert.Equal("\e[A\e[3B", Render(w => w.MoveUp().MoveDown(3)));
        Assert.Equal("", Render(w => w.MoveRight(0)));
        Assert.Equal("\e[12345;1H", Render(w => w.MoveTo(0, 12344)));
    }

    [Fact]
    public void StylesProduceSgr()
    {
        Assert.Equal("\e[0;1;31;48;2;1;2;3m",
            Render(w => w.SetStyle(new Style(Color.Red, Color.Rgb(1, 2, 3), TextAttributes.Bold))));
        Assert.Equal("\e[91m", Render(w => w.SetForeground(Color.BrightRed)));
        Assert.Equal("\e[48;5;200m", Render(w => w.SetBackground(Color.Indexed(200))));
    }

    [Fact]
    public void ColorsDownsample()
    {
        Assert.Equal("\e[38;5;196m", Render(w => w.SetForeground(Color.Rgb(255, 0, 0)), ColorMode.Indexed256));
        Assert.Equal("\e[91m", Render(w => w.SetForeground(Color.Rgb(255, 0, 0)), ColorMode.Basic16));
        Assert.Equal("", Render(w => w.SetForeground(Color.Rgb(255, 0, 0)), ColorMode.None));
        Assert.Equal(232, Color.Rgb(8, 8, 8).Downsample(ColorMode.Indexed256).Index);
        Assert.Equal(16, Color.Rgb(0, 0, 0).Downsample(ColorMode.Indexed256).Index);
    }

    [Fact]
    public void TransitionIsMinimal()
    {
        var a = new Style(Color.Red, default, TextAttributes.Bold | TextAttributes.Underline);
        var b = new Style(Color.Red, default, TextAttributes.Underline);
        Assert.Equal("\e[22m", Render(w => w.TransitionStyle(a, b)));

        var c = new Style(Color.Red, default, TextAttributes.Dim);
        Assert.Equal("\e[22;24;2m", Render(w => w.TransitionStyle(a, c)));

        Assert.Equal("\e[32m", Render(w => w.TransitionStyle(Color.Red, Color.Green)));
        Assert.Equal("", Render(w => w.TransitionStyle(a, a)));
        Assert.Equal("\e[0m", Render(w => w.TransitionStyle(a, default)));
        // Equal after downsampling: nothing to send.
        Assert.Equal("", Render(w => w.TransitionStyle(Color.Rgb(255, 0, 0), Color.Rgb(250, 0, 0)), ColorMode.Basic16));
    }

    [Fact]
    public void LegacyBrightColorsUseBoldAndBlink()
    {
        var w = new AnsiWriter { ColorMode = ColorMode.Basic16, LegacyBrightColors = true };
        w.SetStyle(new Style(Color.BrightYellow, Color.BrightBlue));
        Assert.Equal("\e[0;1;33;5;44m", Encoding.ASCII.GetString(w.WrittenSpan));
    }

    [Fact]
    public void Cp437EncodingAndFallbacks()
    {
        var w = new AnsiWriter { Encoding = TextEncoding.Cp437 };
        w.Write("╔═╗ é ╭ 日");
        Assert.Equal(new byte[] { 0xC9, 0xCD, 0xBB, 0x20, 0x82, 0x20, 0xDA, 0x20, (byte)'?' }, w.WrittenSpan.ToArray());
    }

    [Fact]
    public void Utf8TextAndRunes()
    {
        Assert.Equal("héllo 😀", Render(w => w.Write("héllo ").Write(new Rune(0x1F600))));
        Assert.Equal("-42 0 1000000", Render(w => w.Write(-42).Write(' ').Write(0).Write(' ').Write(1000000)));
    }

    [Fact]
    public void OscStripsControlCharacters()
    {
        Assert.Equal("\e]0;hi there\e\\", Render(w => w.SetTitle("hi\e there\a")));
        Assert.Equal("\e]8;;https://x.y\e\\link\e]8;;\e\\", Render(w => w.Hyperlink("https://x.y", "link")));
    }

    [Fact]
    public void FlushSendsToSinkAndCounts()
    {
        var ms = new MemoryStream();
        var w = new AnsiWriter(ms);
        w.Write("abc").Flush();
        w.Write("de").Flush();
        Assert.Equal("abcde", Encoding.ASCII.GetString(ms.ToArray()));
        Assert.Equal(5, w.TotalBytesFlushed);
        Assert.Equal(0, w.Length);
    }

    [Fact]
    public void GrowsPastInitialCapacity()
    {
        var w = new AnsiWriter(initialCapacity: 64);
        var big = new string('x', 10_000);
        w.Write(big).Write(big.AsSpan(), new Style(Color.Blue));
        Assert.True(w.Length > 20_000);
    }

    [Fact]
    public void ColorParsing()
    {
        Assert.True(Color.TryParse("#ff8000", out var c));
        Assert.Equal((255, 128, 0), (c.R, c.G, c.B));
        Assert.True(Color.TryParse("#0f0", out c));
        Assert.Equal(Color.Rgb(0, 255, 0), c);
        Assert.True(Color.TryParse("42", out c));
        Assert.Equal(Color.Indexed(42), c);
        Assert.False(Color.TryParse("nope", out _));
    }

    [Fact]
    public void Cp437TableIsComplete()
    {
        for (int b = 0x80; b <= 0xFF; b++)
        {
            char ch = Cp437.Decode((byte)b);
            Assert.True(Cp437.TryEncode(new Rune(ch), out var back));
            Assert.Equal(b, back);
        }
        Assert.Equal('☺', Cp437.Decode(1, controlGlyphs: true));
    }
}
