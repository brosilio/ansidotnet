using System;

namespace AnsiDotNet.Layout;

/// <summary>Where a style starts in a <see cref="RichText"/>. It applies until the next run.</summary>
public readonly record struct StyleRun(int Start, Style Style);

/// <summary>
/// Text with styled runs, appended piece by piece. Reuses its buffers across <see cref="Clear"/>, so rebuilding it
/// every frame doesn't allocate. Default colors in a run inherit from the <see cref="TextBlock"/> drawing it.
/// </summary>
public sealed class RichText
{
    private char[] _chars;
    private int _length;
    private StyleRun[] _runs = new StyleRun[8];
    private int _runCount;

    public RichText(int capacity = 64) => _chars = new char[Math.Max(16, capacity)];

    public RichText(string text, Style style = default) : this(text.Length) => Append(text, style);

    public int Length => _length;

    /// <summary>Changes whenever the content changes, so caches can tell.</summary>
    public int Version { get; private set; }

    public ReadOnlySpan<char> Span => _chars.AsSpan(0, _length);
    public ReadOnlySpan<StyleRun> Runs => _runs.AsSpan(0, _runCount);

    public RichText Append(ReadOnlySpan<char> text, Style style = default)
    {
        if (text.IsEmpty)
            return this;
        if (_runCount == 0 || _runs[_runCount - 1].Style != style)
        {
            if (_runCount > 0 && _runs[_runCount - 1].Start == _length)
                _runCount--; // the previous run never got any text
            if (_runCount == _runs.Length)
                Array.Resize(ref _runs, _runs.Length * 2);
            _runs[_runCount++] = new StyleRun(_length, style);
        }
        if (_length + text.Length > _chars.Length)
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _length + text.Length));
        text.CopyTo(_chars.AsSpan(_length));
        _length += text.Length;
        Version++;
        return this;
    }

    public RichText Append(string? text, Style style = default) => Append(text.AsSpan(), style);

    public RichText Append(char c, Style style = default) => Append([c], style);

    public RichText AppendLine(ReadOnlySpan<char> text = default, Style style = default) => Append(text, style).Append('\n', style);

    public void Clear()
    {
        _length = 0;
        _runCount = 0;
        Version++;
    }

    /// <summary>Drops the first <paramref name="count"/> chars, e.g. to keep a log bounded.</summary>
    public void RemoveStart(int count)
    {
        count = Math.Clamp(count, 0, _length);
        if (count == 0)
            return;
        Array.Copy(_chars, count, _chars, 0, _length - count);
        _length -= count;
        int keepFrom = Math.Max(0, RunIndexAt(count));
        int k = 0;
        for (int i = keepFrom; i < _runCount; i++)
            _runs[k++] = new StyleRun(Math.Max(0, _runs[i].Start - count), _runs[i].Style);
        _runCount = _length == 0 ? 0 : k;
        Version++;
    }

    public Style StyleAt(int index)
    {
        int i = RunIndexAt(index);
        return i < 0 ? default : _runs[i].Style;
    }

    internal int RunIndexAt(int index)
    {
        int lo = 0, hi = _runCount - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_runs[mid].Start <= index)
            {
                found = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }
        return found;
    }

    public override string ToString() => new(Span);
}
