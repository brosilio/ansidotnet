using System.Text;

namespace AnsiDotNet.Tests;

public class ParserTests
{
    private static List<InputEvent> Parse(string input, bool flush = false, TextEncoding enc = TextEncoding.Utf8)
    {
        var p = new InputParser { Encoding = enc };
        p.Feed(Encoding.UTF8.GetBytes(input));
        if (flush)
            p.FlushPending();
        var list = new List<InputEvent>();
        while (p.TryRead(out var e))
            list.Add(e);
        return list;
    }

    private static InputEvent Single(string input, bool flush = false) => Assert.Single(Parse(input, flush));

    [Theory]
    [InlineData("\e[A", Key.Up, KeyModifiers.None)]
    [InlineData("\eOB", Key.Down, KeyModifiers.None)]
    [InlineData("\e[1;5C", Key.Right, KeyModifiers.Control)]
    [InlineData("\e[1;3D", Key.Left, KeyModifiers.Alt)]
    [InlineData("\e[1;2H", Key.Home, KeyModifiers.Shift)]
    [InlineData("\e[4~", Key.End, KeyModifiers.None)]
    [InlineData("\e[5;5~", Key.PageUp, KeyModifiers.Control)]
    [InlineData("\e[3~", Key.Delete, KeyModifiers.None)]
    [InlineData("\eOP", Key.F1, KeyModifiers.None)]
    [InlineData("\e[15~", Key.F5, KeyModifiers.None)]
    [InlineData("\e[24;2~", Key.F12, KeyModifiers.Shift)]
    [InlineData("\e[[B", Key.F2, KeyModifiers.None)]
    [InlineData("\e[Z", Key.Tab, KeyModifiers.Shift)]
    [InlineData("\r", Key.Enter, KeyModifiers.None)]
    [InlineData("\x7f", Key.Backspace, KeyModifiers.None)]
    [InlineData("\t", Key.Tab, KeyModifiers.None)]
    [InlineData("\e[13;2u", Key.Enter, KeyModifiers.Shift)]
    public void SpecialKeys(string input, Key key, KeyModifiers mods)
    {
        var e = Single(input);
        Assert.Equal(InputEventKind.Key, e.Kind);
        Assert.Equal(key, e.Key);
        Assert.Equal(mods, e.Modifiers);
    }

    [Fact]
    public void CharactersAndControlCombos()
    {
        var ev = Parse("aÉ日😀\x03\x1a");
        Assert.Equal(6, ev.Count);
        Assert.True(ev[0].IsChar('a'));
        Assert.Equal(0xC9, ev[1].Rune.Value);
        Assert.Equal(0x65E5, ev[2].Rune.Value);
        Assert.Equal(0x1F600, ev[3].Rune.Value);
        Assert.True(ev[4].IsCtrl('c'));
        Assert.True(ev[5].IsCtrl('Z'));
    }

    [Fact]
    public void AltCombos()
    {
        Assert.True(Single("\ex").IsChar('x', KeyModifiers.Alt));
        Assert.True(Single("\e\r").IsKey(Key.Enter, KeyModifiers.Alt));
        var e = Single("\e\x01");
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, e.Modifiers);
    }

    [Fact]
    public void LoneEscapeWaitsThenResolves()
    {
        var p = new InputParser();
        p.Feed("\e"u8);
        Assert.Equal(0, p.Count);
        Assert.True(p.HasPendingInput);
        p.FlushPending();
        Assert.True(p.TryRead(out var e));
        Assert.True(e.IsKey(Key.Escape));
        Assert.False(p.HasPendingInput);
    }

    [Fact]
    public void FlushedPartialCsiBecomesAltBracket()
    {
        var ev = Parse("\e[", flush: true);
        Assert.Single(ev);
        Assert.True(ev[0].IsChar('[', KeyModifiers.Alt));
    }

    [Fact]
    public void SequencesSplitAcrossReads()
    {
        var p = new InputParser();
        var bytes = Encoding.UTF8.GetBytes("\e[1;5A日\e[<0;10;5M");
        foreach (var b in bytes)
            p.Feed([b]);
        Assert.True(p.TryRead(out var a) && a.IsKey(Key.Up, KeyModifiers.Control));
        Assert.True(p.TryRead(out var c) && c.Rune.Value == 0x65E5);
        Assert.True(p.TryRead(out var m) && m.Kind == InputEventKind.Mouse && m.X == 9 && m.Y == 4);
        Assert.False(p.TryRead(out _));
    }

    [Fact]
    public void SgrMouse()
    {
        var press = Single("\e[<0;5;3M");
        Assert.Equal((MouseButton.Left, MouseAction.Press, 4, 2), (press.Button, press.Action, press.X, press.Y));
        var release = Single("\e[<0;5;3m");
        Assert.Equal(MouseAction.Release, release.Action);
        var drag = Single("\e[<32;6;3M");
        Assert.Equal((MouseButton.Left, MouseAction.Drag), (drag.Button, drag.Action));
        var move = Single("\e[<35;6;3M");
        Assert.Equal((MouseButton.None, MouseAction.Move), (move.Button, move.Action));
        var wheel = Single("\e[<65;1;1M");
        Assert.Equal((MouseButton.WheelDown, MouseAction.Wheel), (wheel.Button, wheel.Action));
        var ctrlRight = Single("\e[<18;1;1M");
        Assert.Equal((MouseButton.Right, KeyModifiers.Control), (ctrlRight.Button, ctrlRight.Modifiers));
    }

    [Fact]
    public void X10Mouse()
    {
        var e = Single("\e[M" + (char)(32 + 0) + (char)(33 + 10) + (char)(33 + 4));
        Assert.Equal((MouseButton.Left, MouseAction.Press, 10, 4), (e.Button, e.Action, e.X, e.Y));
    }

    [Fact]
    public void BracketedPasteIsOneEvent()
    {
        var p = new InputParser();
        p.Feed("x\e[200~hello\r\n"u8);
        p.Feed("wor"u8);
        p.Feed("ld\e[20"u8);
        Assert.True(p.TryRead(out var x) && x.IsChar('x'));
        Assert.False(p.TryRead(out _));
        Assert.False(p.HasPendingInput); // an unfinished paste is not an ambiguous escape
        p.Feed("1~y"u8);
        Assert.True(p.TryRead(out var paste));
        Assert.Equal(InputEventKind.Paste, paste.Kind);
        Assert.Equal("hello\nworld", paste.Text);
        Assert.True(p.TryRead(out var y) && y.IsChar('y'));
    }

    [Fact]
    public void PasteDoesNotInterpretEscapes()
    {
        var e = Single("\e[200~a\e[Ab\e[201~");
        Assert.Equal("a\e[Ab", e.Text);
    }

    [Fact]
    public void TelnetCrLfIsOneEnter()
    {
        var ev = Parse("a\r\nb\r\0c");
        Assert.Equal(5, ev.Count);
        Assert.True(ev[1].IsKey(Key.Enter));
        Assert.True(ev[3].IsKey(Key.Enter));
        Assert.True(ev[4].IsChar('c'));
    }

    [Fact]
    public void CursorReportOnlyWhenExpected()
    {
        Assert.True(Single("\e[1;5R").IsKey(Key.F3, KeyModifiers.Control));

        var p = new InputParser();
        p.ExpectCursorPositionReport();
        p.Feed("\e[12;40R"u8);
        Assert.True(p.TryRead(out var e));
        Assert.Equal((InputEventKind.CursorPosition, 39, 11), (e.Kind, e.X, e.Y));
    }

    [Fact]
    public void RepliesAreIgnored()
    {
        Assert.Empty(Parse("\e]11;rgb:0000/0000/0000\e\\"));
        Assert.Empty(Parse("\e[?62;22c"));
        Assert.True(Single("\e]10;x\aq").IsChar('q'));
    }

    [Fact]
    public void FocusEvents()
    {
        var ev = Parse("\e[I\e[O");
        Assert.Equal(InputEventKind.FocusIn, ev[0].Kind);
        Assert.Equal(InputEventKind.FocusOut, ev[1].Kind);
    }

    [Fact]
    public void ModifyOtherKeysAndKitty()
    {
        Assert.True(Single("\e[27;5;105~").IsChar('i', KeyModifiers.Control));
        Assert.True(Single("\e[97;7u").IsChar('a', KeyModifiers.Control | KeyModifiers.Alt));
    }

    [Fact]
    public void Cp437Input()
    {
        var p = new InputParser { Encoding = TextEncoding.Cp437 };
        p.Feed([0x82, 0xDB]);
        Assert.True(p.TryRead(out var a) && a.Rune.Value == 'é');
        Assert.True(p.TryRead(out var b) && b.Rune.Value == '█');
    }

    [Fact]
    public void InvalidUtf8BecomesReplacement()
    {
        var p = new InputParser();
        p.Feed([0xFF, (byte)'a']);
        Assert.True(p.TryRead(out var e) && e.Rune == Rune.ReplacementChar);
        Assert.True(p.TryRead(out var a) && a.IsChar('a'));
    }

    [Fact]
    public void GarbageDoesNotGrowBufferForever()
    {
        var p = new InputParser();
        p.Feed(Encoding.ASCII.GetBytes("\e[" + new string('1', 500)));
        Assert.False(p.HasPendingInput);
        Assert.True(p.Count > 0);
    }
}
