using System;
using System.Runtime.InteropServices;

namespace AnsiDotNet;

/// <summary>
/// Draws <see cref="Surface"/> frames to an <see cref="AnsiWriter"/>, sending only the cells that changed since the
/// previous frame. Assumes it owns the whole screen, starting at the top-left corner.
/// </summary>
public sealed class ScreenRenderer
{
    private readonly AnsiWriter _out;
    private Cell[] _front = [];
    private Cell[] _scratch = [];
    private int _width, _height;
    private bool _invalid = true;
    private Style _pen;
    private int _cx = -1, _cy = -1;
    private bool? _cursorShown;
    private (int X, int Y) _cursorAt = (-1, -1);

    public ScreenRenderer(AnsiWriter output) => _out = output;

    /// <summary>Wraps frames in DEC mode 2026 so supporting terminals display them atomically.</summary>
    public bool SynchronizedOutput { get; set; } = true;

    /// <summary>Where to leave a visible cursor after each frame; null hides it.</summary>
    public (int X, int Y)? Cursor { get; set; }

    public int LastFrameBytes { get; private set; }
    public int LastFrameCellsChanged { get; private set; }
    public long FrameCount { get; private set; }

    /// <summary>Forces the next frame to repaint everything (after a resize, or if something else wrote to the screen).</summary>
    public void Invalidate() => _invalid = true;

    /// <summary>Draws the frame and flushes the writer.</summary>
    public void Render(Surface frame, bool flush = true)
    {
        int startLen = _out.Length;
        long startTotal = _out.TotalBytesFlushed;
        var (startCx, startCy, startShown, startAt) = (_cx, _cy, _cursorShown, _cursorAt);

        if (SynchronizedOutput)
            _out.BeginSynchronizedUpdate();
        if (_cursorShown != false)
        {
            _out.HideCursor();
            _cursorShown = false;
        }

        bool full = _invalid || frame.Width != _width || frame.Height != _height;
        LastFrameCellsChanged = full ? FullRedraw(frame) : DiffRedraw(frame);
        _invalid = false;

        if (_pen != default)
        {
            _out.ResetStyle();
            _pen = default;
        }
        bool cursorVisible = Cursor is { } c && c.X >= 0 && c.Y >= 0 && c.X < _width && c.Y < _height;
        if (cursorVisible)
        {
            var at = Cursor!.Value;
            MoveTo(at.X, at.Y);
            _out.ShowCursor();
            _cursorShown = true;
            _cursorAt = at;
        }
        if (SynchronizedOutput)
            _out.EndSynchronizedUpdate();

        // Nothing changed on screen and the cursor is where it was: send nothing at all, not even the wrappers.
        bool unchanged = !full && LastFrameCellsChanged == 0 && _out.TotalBytesFlushed == startTotal &&
            (cursorVisible ? startShown == true && startAt == _cursorAt : startShown == false);
        if (unchanged)
        {
            _out.Truncate(startLen);
            (_cx, _cy, _cursorShown, _cursorAt) = (startCx, startCy, startShown, startAt);
        }

        LastFrameBytes = (int)(_out.Length - startLen + (_out.TotalBytesFlushed - startTotal));
        FrameCount++;
        if (flush)
            _out.Flush();
    }

    private int FullRedraw(Surface frame)
    {
        _width = frame.Width;
        _height = frame.Height;
        if (_front.Length != _width * _height)
            _front = new Cell[_width * _height];

        _out.ResetStyle();
        _pen = default;
        _out.ClearScreen();
        _cx = _cy = -1;

        var cells = frame.Cells;
        for (int y = 0; y < _height; y++)
        {
            var row = PrepareRow(cells.Slice(y * _width, _width));
            // Rows ending in default blanks don't need those cells written after a clear.
            int end = _width;
            while (end > 0 && row[end - 1] == Cell.Blank)
                end--;
            for (int x = 0; x < end; x++)
            {
                if (row[x].IsContinuation)
                    continue;
                WriteCell(row, x, y);
            }
            row.CopyTo(_front.AsSpan(y * _width, _width));
        }
        return _width * _height;
    }

    private int DiffRedraw(Surface frame)
    {
        var cells = frame.Cells;
        int changed = 0;
        for (int y = 0; y < _height; y++)
        {
            var row = PrepareRow(cells.Slice(y * _width, _width));
            var front = _front.AsSpan(y * _width, _width);
            if (MemoryMarshal.AsBytes(row).SequenceEqual(MemoryMarshal.AsBytes(front)))
                continue;

            int lastWritten = -1;
            for (int x = 0; x < _width; x++)
            {
                if (row[x] == front[x])
                    continue;
                changed++;
                int at = x;
                if (row[x].IsContinuation)
                {
                    if (lastWritten == x - 1 || x == 0)
                        continue;
                    at = x - 1;
                }
                SkipTo(row, at, y);
                WriteCell(row, at, y);
                lastWritten = at;
            }
            row.CopyTo(front);
        }
        return changed;
    }

    // The front buffer holds cells as they were actually written, so colors that downsample or round to what is
    // already on screen count as unchanged and are skipped.
    private ReadOnlySpan<Cell> PrepareRow(ReadOnlySpan<Cell> row)
    {
        if (_out.ColorsExact)
            return row;
        if (_scratch.Length < row.Length)
            _scratch = new Cell[row.Length];
        var dst = _scratch.AsSpan(0, row.Length);
        for (int i = 0; i < row.Length; i++)
        {
            var c = row[i];
            dst[i] = c.Style == default ? c : new Cell(c.Rune, _out.Downsampled(c.Style));
        }
        return dst;
    }

    // Moves to (x,y), reprinting a few unchanged cells when that is cheaper than a cursor sequence.
    private void SkipTo(ReadOnlySpan<Cell> row, int x, int y)
    {
        if (_cy == y && _cx >= 0 && x > _cx && x - _cx <= 3)
        {
            bool cheap = true;
            for (int i = _cx; i < x; i++)
            {
                var c = row[i];
                if (c.Style != _pen || c.Rune.Value >= 0x80 || c.Rune.Value < 0x20)
                {
                    cheap = false;
                    break;
                }
            }
            if (cheap)
            {
                for (int i = _cx; i < x; i++)
                    _out.Write(row[i].Rune);
                _cx = x;
                return;
            }
        }
        MoveTo(x, y);
    }

    private void MoveTo(int x, int y)
    {
        if (_cx == x && _cy == y)
            return;
        if (_cy == y && _cx >= 0 && x > _cx)
            _out.MoveRight(x - _cx);
        else if (x == 0 && _cy >= 0 && y == _cy + 1)
            _out.Raw("\r\n"u8);
        else
            _out.MoveTo(x, y);
        _cx = x;
        _cy = y;
    }

    private void WriteCell(ReadOnlySpan<Cell> row, int x, int y)
    {
        var cell = row[x];
        MoveTo(x, y);
        if (cell.Style != _pen)
        {
            _out.TransitionStyle(_pen, cell.Style);
            _pen = cell.Style;
        }
        bool wide = x + 1 < row.Length && row[x + 1].IsContinuation;
        int v = cell.Rune.Value;
        if (v < 0x20 || (v >= 0x7F && v < 0xA0))
            _out.Write(' '); // never let raw control bytes through
        else
            _out.Write(cell.Rune);
        if (wide)
            _cx = -1; // terminals disagree on some widths; re-anchor next write
        else if (++_cx >= _width)
            _cx = -1; // pending-wrap state is not something to rely on
    }
}
