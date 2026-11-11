# AnsiDotNet

A zero-dependency ANSI/VT terminal library for .NET 8+, built for NativeAOT and trimming (no reflection, source-generated P/Invoke). Use it for games with high data rates, BBS and telnet servers, and full-screen TUIs.

```csharp
using AnsiDotNet;

using var term = Terminal.OpenConsole(new TerminalOptions { Mouse = MouseMode.Drag });
var screen = new Surface(term.Size);

while (true)
{
    screen.Clear();
    screen.DrawBox(screen.Bounds, Color.Cyan, BoxStyle.Rounded, "hello");
    screen.Print(2, 2, "press q to quit", new Style(Color.Rgb(255, 180, 0)));
    term.Render(screen);                       // diffed; only changed cells are sent

    if (!term.TryReadEvent(out var e, TimeSpan.FromMilliseconds(16)))
        continue;                              // ~60 fps tick
    if (e.IsChar('q') || e.Kind == InputEventKind.Closed) break;
    if (e.Kind == InputEventKind.Resize) screen.Resize(e.Width, e.Height);
}
// Dispose restores the tty mode, leaves the alternate screen, and turns off mouse and paste reporting
```

## Layers

You can use each layer on its own. Only `Terminal` touches the OS.

| Type | What it does |
|---|---|
| `AnsiWriter` | Buffers text and escape sequences as encoded bytes. Covers cursor, erase, scroll regions, SGR styles, DEC private modes, OSC title, hyperlinks and clipboard. Writes into a growable `byte[]` (also an `IBufferWriter<byte>`) and flushes to a `Stream` or an `IOutputSink`. Downsamples colors to the target `ColorMode` and encodes text as UTF-8, CP437 or ASCII. |
| `InputParser` | Turns raw input bytes into `InputEvent`s. Handles keys with modifiers (xterm, SS3, Linux console, kitty CSI‑u, modifyOtherKeys), SGR/X10/urxvt mouse, bracketed paste, focus and cursor reports. Works on bytes from any source. |
| `Surface` | A cell grid you draw into: `Print`, `PrintAligned`, `Fill`, `DrawBox`, `Blit`, clipping, wide characters, `PrintAnsi` to render ANSI art or colored output into cells, and `Blend`/`Fade` for compositing with opacity. |
| `ScreenRenderer` | Diffs each `Surface` frame against the previous one and sends the minimum bytes. It keeps track of the cursor, picks the cheapest way to move it, does minimal SGR transitions, and wraps frames in synchronized output (mode 2026). |
| `Terminal` | A session over an `ITerminalBackend`. Sets up and restores terminal modes, queues input events with an escape timeout, and offers sync (`TryReadEvent`) and async (`ReadEventAsync`) reads. |
| Console backend (`Terminal.OpenConsole`) | Raw mode on Linux, macOS and FreeBSD (termios, `poll`, SIGWINCH), or VT mode on Windows 10+ (console modes, UTF-8 code page). |
| `StreamBackend` | Any `Stream` pair: TCP, telnet, SSH channels, pipes. You report the remote window size with `SetSize`. |
| `AnsiTokenizer` / `AnsiText` | Allocation-free tokenizing of strings that contain escape sequences. Includes `Strip`, `VisibleWidth` and `ApplySgr`. |
| `CellWidth`, `Cp437`, `Color`, `Style` | Supporting types: column-width lookup (wcwidth), code page 437 conversion, colors packed into 4 bytes (palette / 256 / RGB, HSV, lerp), and styles. |

## Layout and animation

`AnsiDotNet.Layout` (in `src/AnsiDotNet.Layout`) is a companion library with three parts:
- **Constraint layout:** fixed/percent/fill/auto sizes, plus stack, dock and layer containers.
- **Text layout:** wrapping, justify, ellipsis, rich text, smooth scrolling.
- **Animation:** slide, fade (real compositing), expand/collapse, wipe, shake, typewriter, and layout transitions that make docking, undocking and reflows glide.

It doesn't allocate per frame either. See [its README](src/AnsiDotNet.Layout/README.md).

```csharp
var stage = new Stage();
stage.Root.Add(new TextNode(log) { FollowTail = true, Border = BoxStyle.Rounded, Title = "log" });
stage.Render(screen, elapsed);
term.Render(screen);
```

## Network sessions (BBS, telnet)

```csharp
var backend = new StreamBackend(networkStream, new TerminalSize(80, 24));
using var term = Terminal.Open(backend, new TerminalOptions
{
    EscapeTimeout = TimeSpan.FromMilliseconds(150),   // network jitter splits sequences
    Encoding = TextEncoding.Cp437,                    // classic clients
    ColorMode = ColorMode.Basic16,
});
term.Output.LegacyBrightColors = true;                // bright via bold/blink, for ANSI.SYS-era clients
// later, when the client reports its window size (telnet NAWS, SSH window-change):
backend.SetSize(new TerminalSize(cols, rows));
```

The parser treats CR LF and CR NUL as a single Enter, as telnet clients send them. The telnet protocol itself (IAC handling, option negotiation) isn't ANSI, so it's out of scope here. The demo has a ~150-line implementation.

## Behavior notes

- Coordinates are zero-based everywhere (`x` = column, `y` = row).
- `Terminal.OpenConsole` restores the terminal on `Dispose`, on process exit and on unhandled exceptions. `Suspend()`/`Resume()` hand the terminal back temporarily, for example to run `$EDITOR`.
- Color detection follows `NO_COLOR`, `FORCE_COLOR`, `COLORTERM` and `TERM`. You can override it with `TerminalOptions.ColorMode`, and change `Terminal.ColorMode` at any time.
- A lone ESC can't be told apart from the start of a sequence until more bytes arrive or `EscapeTimeout` passes (50 ms by default). Kitty keyboard mode (`KittyKeyboard = true`) removes the ambiguity where it's supported.
- Threading: input is read on a background thread and queued. Read events from one thread, and use `Output`/`Renderer` from one thread. Other threads can call `Terminal.Wake()` to make the reader return an `InputEventKind.Wake` event (coalesced), so a session loop can sleep until input *or* a change in shared state, instead of polling.
- For high frame rates the terminal is usually the bottleneck, not the renderer. Fewer bytes means more fps: set `Output.TrueColorPrecision` to 5 or 6 bits per channel, and colors that round to what is already on screen are skipped entirely (2.5× fewer bytes on a full-screen gradient at 5 bits). Downsampled color modes get the same skipping.
- Wide characters (CJK, emoji) take two cells. Combining marks are dropped rather than composed.

## Building

```
dotnet build
dotnet test
```

Targets `net8.0` and `net10.0`. Both libraries have `IsAotCompatible` set, and the demo publishes as a ~2.6 MB native binary with no trim/AOT warnings.

## License

[0BSD](LICENSE). Do whatever you want with it.
