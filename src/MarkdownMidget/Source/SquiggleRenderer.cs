using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MarkdownMidget.Source;

/// <summary>
/// Red wavy underlines for the source view, drawn from host-computed misspelling
/// ranges.
///
/// An AvalonEdit background renderer rather than a WPF adorner: the text view calls
/// <see cref="Draw"/> as part of its own render pass, so the squiggles repaint with
/// the text on every scroll and edit without a separate invalidation path, and the
/// geometry comes from <see cref="BackgroundGeometryBuilder.GetRectsForSegment"/> in
/// the view's own coordinates — no hand-rolled per-character math, and nothing to get
/// wrong about scroll offsets. The renderer never hit-tests, so typing over it feels
/// exactly as it did.
/// </summary>
internal sealed class SquiggleRenderer : IBackgroundRenderer
{
    private static readonly Pen WavePen = MakePen();
    private readonly TextView _view;
    private IReadOnlyList<(int Start, int Length)> _ranges = Array.Empty<(int, int)>();

    public SquiggleRenderer(SourceEditor editor)
    {
        _view = editor.TextArea.TextView;
    }

    private static Pen MakePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x1D, 0x1D)), 1.2);
        pen.Freeze();
        return pen;
    }

    /// <summary>Drawn just above the selection so a highlighted misspelling still
    /// shows its underline.</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The misspelled ranges currently drawn, in document offsets. These are
    /// the authoritative answer to "what exactly is the misspelled token" — they come
    /// from checking the whole document, and re-checking a word in isolation gives
    /// different answers (a repeated word stops being an error; hyphen and apostrophe
    /// boundaries move).</summary>
    public IReadOnlyList<(int Start, int Length)> Ranges => _ranges;

    public void SetRanges(IReadOnlyList<(int Start, int Length)> ranges)
    {
        _ranges = ranges;
        _view.InvalidateLayer(Layer);
    }

    /// <summary>Shift ranges through a text edit so squiggles stay glued to their
    /// words until the next re-check; ranges the edit touched are dropped.</summary>
    public void ShiftForEdit(int offset, int added, int removed)
    {
        _ranges = SquiggleRanges.Shift(_ranges, offset, added, removed);
        _view.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        foreach (var (x1, x2, y) in WaveSpans(textView))
            DrawWave(drawingContext, x1, x2, y);
    }

    /// <summary>
    /// The underline spans this renderer would draw for the current ranges against a
    /// laid-out view, in view coordinates: one per visible run of each range, already
    /// clipped to the viewport by AvalonEdit and filtered to those wide enough to
    /// draw. Exposed so the decision logic — clamp to the document, skip empty or
    /// hairline runs, nothing for off-screen ranges — is testable without a
    /// DrawingContext.
    /// </summary>
    internal IReadOnlyList<(double X1, double X2, double Y)> WaveSpans(TextView textView)
    {
        var spans = new List<(double, double, double)>();
        if (_ranges.Count == 0 || !textView.VisualLinesValid) return spans;
        var docLength = textView.Document?.TextLength ?? 0;

        foreach (var (start, len) in _ranges)
        {
            if (len <= 0) continue;
            var from = Math.Clamp(start, 0, docLength);
            var to = Math.Clamp(start + len, 0, docLength);
            if (to <= from) continue;

            var segment = new TextSegment { StartOffset = from, EndOffset = to };
            // GetRectsForSegment returns only the parts of the segment on visible
            // lines, already in view coordinates — so a range scrolled off-screen
            // contributes nothing and one that straddles the edge is clipped for us.
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                if (r.Right - r.Left >= 1.5)
                    spans.Add((r.Left, r.Right, r.Bottom - 1.0));
        }
        return spans;
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
