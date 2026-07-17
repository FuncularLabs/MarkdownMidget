using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarkdownMidget.Spelling;

/// <summary>
/// Red wavy underlines for the source-view <see cref="TextBox"/>, drawn from
/// host-computed misspelling ranges. Only ranges intersecting the visible lines
/// are rendered (same viewport-limiting requirement the WYSIWYG side has), and
/// the overlay never hit-tests so editing feels identical with it up.
/// </summary>
internal sealed class SquiggleAdorner : Adorner
{
    private static readonly Pen WavePen = MakePen();
    private IReadOnlyList<(int Start, int Length)> _ranges = Array.Empty<(int, int)>();
    private readonly TextBox _box;

    public SquiggleAdorner(TextBox box) : base(box)
    {
        _box = box;
        IsHitTestVisible = false;
    }

    private static Pen MakePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x1D, 0x1D)), 1.2);
        pen.Freeze();
        return pen;
    }

    public void SetRanges(IReadOnlyList<(int Start, int Length)> ranges)
    {
        _ranges = ranges;
        InvalidateVisual();
    }

    /// <summary>Shift ranges through a text edit so squiggles stay glued to their
    /// words until the next (debounced) re-check; ranges the edit touched are dropped.</summary>
    public void ShiftForEdit(int offset, int added, int removed)
    {
        if (_ranges.Count == 0) return;
        var updated = new List<(int, int)>(_ranges.Count);
        var delta = added - removed;
        foreach (var (start, len) in _ranges)
        {
            if (start + len <= offset) { updated.Add((start, len)); continue; }   // before the edit
            if (start >= offset + removed) { updated.Add((start + delta, len)); continue; } // after
            // the edit overlapped this word — it's stale, drop it
        }
        _ranges = updated;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_ranges.Count == 0 || _box.ActualWidth <= 0) return;

        var firstLine = _box.GetFirstVisibleLineIndex();
        var lastLine = _box.GetLastVisibleLineIndex();
        if (firstLine < 0) return;
        if (lastLine < firstLine) lastLine = firstLine;

        int visStart, visEnd;
        try
        {
            visStart = _box.GetCharacterIndexFromLineIndex(firstLine);
            visEnd = lastLine + 1 < _box.LineCount
                ? _box.GetCharacterIndexFromLineIndex(lastLine + 1)
                : _box.Text.Length;
        }
        catch { return; }
        if (visStart < 0) return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, _box.ActualWidth, _box.ActualHeight)));
        try
        {
            foreach (var (start, len) in _ranges)
            {
                if (start + len < visStart) continue;
                if (start > visEnd) break;              // ranges arrive sorted
                DrawRange(dc, start, Math.Min(start + len, _box.Text.Length));
            }
        }
        finally { dc.Pop(); }
    }

    private void DrawRange(DrawingContext dc, int start, int end)
    {
        if (end <= start) return;
        int lineA, lineB;
        try
        {
            lineA = _box.GetLineIndexFromCharacterIndex(start);
            lineB = _box.GetLineIndexFromCharacterIndex(end - 1);
        }
        catch { return; }
        if (lineA < 0 || lineB < lineA) return;

        for (var line = lineA; line <= lineB; line++)
        {
            int lineStart, lineEnd;
            try
            {
                lineStart = _box.GetCharacterIndexFromLineIndex(line);
                lineEnd = line + 1 < _box.LineCount
                    ? _box.GetCharacterIndexFromLineIndex(line + 1)
                    : _box.Text.Length;
            }
            catch { continue; }

            var segStart = Math.Max(start, lineStart);
            var segEnd = Math.Min(end, lineEnd);
            if (segEnd <= segStart) continue;

            Rect r1, r2;
            try
            {
                r1 = _box.GetRectFromCharacterIndex(segStart);
                r2 = _box.GetRectFromCharacterIndex(segEnd);
            }
            catch { continue; }
            if (r1.IsEmpty || double.IsInfinity(r1.X) || double.IsInfinity(r2.X)) continue;

            // A segment ending in a newline reports the next line's leading edge —
            // fall back to the trailing edge of its own last char.
            var x2 = r2.X;
            if (r2.IsEmpty || Math.Abs(r2.Y - r1.Y) > 0.5 || x2 <= r1.X)
            {
                try
                {
                    var lastRect = _box.GetRectFromCharacterIndex(Math.Max(segStart, segEnd - 1), true);
                    if (lastRect.IsEmpty || Math.Abs(lastRect.Y - r1.Y) > 0.5) continue;
                    x2 = lastRect.X;
                }
                catch { continue; }
            }
            if (x2 - r1.X < 1.5) continue;
            DrawWave(dc, r1.X, x2, r1.Bottom - 1.5);
        }
    }

    private static void DrawWave(DrawingContext dc, double x1, double x2, double y)
    {
        const double step = 2.0, amp = 1.6;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x1, y), false, false);
            var up = true;
            for (var x = x1; x < x2; x += step)
            {
                ctx.LineTo(new Point(Math.Min(x + step, x2), up ? y - amp : y), true, false);
                up = !up;
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, WavePen, geo);
    }
}
