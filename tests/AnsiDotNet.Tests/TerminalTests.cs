using System.IO.Pipes;
using System.Text;

namespace AnsiDotNet.Tests;

public class TerminalTests
{
    private sealed class Harness : IDisposable
    {
        public readonly AnonymousPipeServerStream ToTerminal = new(PipeDirection.Out);
        public readonly AnonymousPipeClientStream TerminalIn;
        public readonly MemoryStream TerminalOut = new();
        public readonly StreamBackend Backend;
        public readonly Terminal Terminal;

        public Harness(TerminalOptions? options = null)
        {
            TerminalIn = new AnonymousPipeClientStream(PipeDirection.In, ToTerminal.ClientSafePipeHandle);
            Backend = new StreamBackend(TerminalIn, new SyncStream(TerminalOut), new TerminalSize(80, 24));
            Terminal = Terminal.Open(Backend, options);
        }

        public void Send(string s)
        {
            ToTerminal.Write(Encoding.UTF8.GetBytes(s));
            ToTerminal.Flush();
        }

        public string Output
        {
            get
            {
                lock (TerminalOut)
                    return Encoding.UTF8.GetString(TerminalOut.ToArray());
            }
        }

        public void Dispose()
        {
            Terminal.Dispose();
            ToTerminal.Dispose();
        }
    }

    private sealed class SyncStream(MemoryStream inner) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (inner)
                inner.Write(buffer, offset, count);
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void SetupAndTeardownSequences()
    {
        var h = new Harness(new TerminalOptions { Mouse = MouseMode.Drag, Title = "t" });
        Assert.Contains("\e[?1049h", h.Output);
        Assert.Contains("\e[?1002h\e[?1006h", h.Output);
        Assert.Contains("\e[?2004h", h.Output);
        h.Dispose();
        string all = h.Output;
        Assert.EndsWith("\e[?1049l", all);
        Assert.Contains("\e[?1006l", all);
        Assert.Contains("\e[?25h", all);
        Assert.Contains("\e[?7h", all);
    }

    [Fact]
    public void EventsFlowThroughQueue()
    {
        using var h = new Harness();
        h.Send("q\e[B");
        Assert.True(h.Terminal.TryReadEvent(out var a, TimeSpan.FromSeconds(5)));
        Assert.True(a.IsChar('q'));
        Assert.True(h.Terminal.TryReadEvent(out var b, TimeSpan.FromSeconds(5)));
        Assert.True(b.IsKey(Key.Down));
        Assert.False(h.Terminal.TryReadEvent(out _, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task WakeIsCoalescedAndRearms()
    {
        using var h = new Harness();
        var pending = h.Terminal.ReadEventAsync(TimeSpan.FromSeconds(5)).AsTask();
        await Task.Run(() => { h.Terminal.Wake(); h.Terminal.Wake(); h.Terminal.Wake(); });
        Assert.Equal(InputEventKind.Wake, (await pending)!.Value.Kind);
        Assert.False(h.Terminal.TryReadEvent(out _, TimeSpan.FromMilliseconds(30))); // the extra wakes coalesced

        h.Terminal.Wake(); // re-armed once the first was read
        Assert.True(h.Terminal.TryReadEvent(out var again, TimeSpan.FromSeconds(5)));
        Assert.Equal(InputEventKind.Wake, again.Kind);
    }

    [Fact]
    public async Task LoneEscapeResolvesAfterTimeout()
    {
        using var h = new Harness(new TerminalOptions { EscapeTimeout = TimeSpan.FromMilliseconds(30) });
        h.Send("\e");
        var ev = await h.Terminal.ReadEventAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(ev);
        Assert.True(ev.Value.IsKey(Key.Escape));
    }

    [Fact]
    public async Task ResizeAndCloseAreEvents()
    {
        using var h = new Harness();
        h.Backend.SetSize(new TerminalSize(100, 30));
        var ev = await h.Terminal.ReadEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(InputEventKind.Resize, ev!.Value.Kind);
        Assert.Equal(new TerminalSize(100, 30), h.Terminal.Size);

        h.ToTerminal.Dispose(); // remote hangs up
        var closed = await h.Terminal.ReadEventAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(InputEventKind.Closed, closed!.Value.Kind);
        Assert.True(h.Terminal.IsClosed);
        Assert.Equal(InputEventKind.Closed, (await h.Terminal.ReadEventAsync()).Kind);
    }

    [Fact]
    public void RenderGoesThroughBackend()
    {
        using var h = new Harness(new TerminalOptions { ColorMode = ColorMode.Basic16 });
        var s = new Surface(h.Terminal.Size);
        s.Print(0, 0, "hi", new Style(Color.Rgb(250, 0, 0)));
        h.Terminal.Render(s);
        Assert.Contains("\e[91mhi", h.Output);
    }
}
