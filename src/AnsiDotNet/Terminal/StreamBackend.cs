using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnsiDotNet;

/// <summary>
/// Backend over arbitrary streams, e.g. a <c>NetworkStream</c> for a telnet/BBS session. The remote size is not
/// knowable from the byte stream alone; report it with <see cref="SetSize"/> (from NAWS, SSH window-change, ...).
/// </summary>
public sealed class StreamBackend : ITerminalBackend
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private ITerminalInput? _sink;
    private TerminalSize _size;
    private int _closed;

    public StreamBackend(Stream input, Stream output, TerminalSize size, bool ownsStreams = true)
    {
        _input = input;
        _output = output;
        _size = size;
        _ownsStreams = ownsStreams;
    }

    public StreamBackend(Stream duplex, TerminalSize size, bool ownsStream = true) : this(duplex, duplex, size, ownsStream) { }

    public TerminalSize Size => _size;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void SetSize(TerminalSize size)
    {
        if (size.Width <= 0 || size.Height <= 0 || size == _size)
            return;
        _size = size;
        _sink?.OnResize(size);
    }

    public void Start(ITerminalInput input)
    {
        if (_sink is not null)
            throw new InvalidOperationException("Already started.");
        _sink = input;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _input.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0)
                    break;
                _sink!.OnInput(buffer.AsSpan(0, n));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
        Close();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (IsClosed)
            return;
        try
        {
            lock (_writeLock)
            {
                _output.Write(data);
                _output.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
            Close();
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            _sink?.OnClosed();
    }

    public void Dispose()
    {
        _cts.Cancel();
        Close();
        if (_ownsStreams)
        {
            _input.Dispose();
            if (!ReferenceEquals(_input, _output))
                _output.Dispose();
        }
        _cts.Dispose();
    }
}
