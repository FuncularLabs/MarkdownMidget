using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MarkdownMidget.Source;

/// <summary>
/// The source view's formatting marks while the ¶ toolbar toggle is on: ¶ at each line
/// ending and → at each tab, the formatted view's glyphs, and · on the spaces
/// <see cref="SpaceMarks"/> picks. AvalonEdit's own marks are not used: its end-of-line
/// mark spells the ending out ("\n" for the LF text held in memory), its tab mark is », and
/// it marks every space or none.
///
/// A background renderer, like <see cref="SquiggleRenderer"/>: it only paints, so the
/// caret, word-by-word moves, selection, wrapping, the status bar's column, Find, copy,
/// print and save never see a mark. Drawn in the selection layer, which repaints on every
/// scroll and edit, so a selected mark sits under the selection tint.
/// </summary>
internal sealed class FormattingMarks : IBackgroundRenderer
{
    private readonly TextView _view;
    private bool _enabled;

    public FormattingMarks(TextView view) => _view = view;

    /// <summary>Whether the marks are drawn. Setting it repaints the view.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _view.InvalidateLayer(Layer);
        }
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The <see cref="SpaceMarks"/> state each line starts in, for the lines read so
    /// far: [n - 1] is line n's. Filled on demand down to the lines on screen, and cut back
    /// by <see cref="TextChanged"/>, so an edit re-reads only from its own line.</summary>
    private readonly List<SpaceMarks.State> _lineStarts = [SpaceMarks.State.Start];

    /// <summary>The document changed at line <paramref name="lineNumber"/>: the states of the
    /// lines after it may be wrong now. Called for every change, marks on or off.</summary>
    internal void TextChanged(int lineNumber)
    {
        if (_lineStarts.Count > lineNumber) _lineStarts.RemoveRange(lineNumber, _lineStarts.Count - lineNumber);
    }

    private SpaceMarks.State StartOf(TextDocument doc, int lineNumber)
    {
        for (var n = _lineStarts.Count; n < lineNumber; n++)
            _lineStarts.Add(SpaceMarks.Step(doc.GetText(doc.GetLineByNumber(n)), _lineStarts[n - 1]).Next);
        return _lineStarts[lineNumber - 1];
    }

    /// <summary>How many tab and space marks the last <see cref="Positions"/> placed, each
    /// the cost of a visual-position lookup.</summary>
    internal int Placed { get; private set; }

    /// <summary>How many wrapped rows the last <see cref="Positions"/> stepped through.</summary>
    internal int RowsVisited { get; private set; }

    /// <summary>
    /// Each visible mark and where it goes in view coordinates (the glyph's top-left): →
    /// where a tab starts, · on the spaces <see cref="SpaceMarks"/> picks, ¶ right after the
    /// text of each line that ends in a line break, on its last wrapped row (the document's
    /// last line has none). Empty when off or before layout. Exposed so placement is
    /// testable without a DrawingContext.
    /// </summary>
    internal IReadOnlyList<(char Glyph, Point At)> Positions(TextView textView)
    {
        Placed = RowsVisited = 0;
        var marks = new List<(char, Point)>();
        if (!_enabled || !textView.VisualLinesValid || textView.Document is not { } doc) return marks;
        var scroll = textView.ScrollOffset;
        foreach (var line in textView.VisualLines)
        {
            var start = line.FirstDocumentLine.Offset;
            int[]? spaces = null;
            // One pass down the wrapped rows, keeping each row's top and first column as it goes:
            // AvalonEdit's per-row lookups walk every row of the line, which on a pasted picture's
            // thousands of rows would cost rows² on every repaint. Rows above the view are stepped
            // over; the first row below it ends the walk.
            double top = line.VisualTop;   // in the document; the scroll comes off last, as AvalonEdit's own positions do
            var first = 0;
            foreach (var row in line.TextLines)
            {
                RowsVisited++;
                double rowTop = top;
                int rowFirst = first;
                top += row.Height;
                first += row.Length;
                if (rowTop - scroll.Y > textView.ActualHeight) break;
                if (top - scroll.Y < 0) continue;
                // A long row is cut to the columns under the view's left and right edges, with one to spare.
                int left = Math.Max(rowFirst, line.GetVisualColumn(row, scroll.X, false) - 1),
                    right = Math.Min(first, line.GetVisualColumn(row, scroll.X + textView.ActualWidth, false) + 1);
                int from = start + line.GetRelativeOffset(left), to = start + line.GetRelativeOffset(right);
                var textTop = rowTop + row.Baseline - textView.DefaultBaseline - scroll.Y;   // VisualYPosition.TextTop, for this row
                Point At(int offset)
                {
                    Placed++;
                    return new Point(line.GetTextLineVisualXPosition(row, line.GetVisualColumn(offset - start)) - scroll.X, textTop);
                }
                for (var tab = doc.IndexOf('\t', from, to - from); tab >= 0; tab = doc.IndexOf('\t', tab + 1, to - tab - 1))
                    marks.Add(('→', At(tab)));
                spaces ??= SpaceMarks.Step(doc.GetText(line.FirstDocumentLine), StartOf(doc, line.FirstDocumentLine.LineNumber)).Marks;
                // Marks come in offset order, so the row's first is found by halving, not by reading them all.
                var index = Array.BinarySearch(spaces, from - start);
                for (index = index < 0 ? ~index : index; index < spaces.Length && start + spaces[index] < to; index++)
                    marks.Add(('·', At(start + spaces[index])));
            }
            if (line.LastDocumentLine.DelimiterLength == 0) continue;
            var last = line.TextLines[^1];   // its top is the line's bottom less its own height
            marks.Add(('¶', new Point(line.GetTextLineVisualXPosition(last, line.VisualLength) - scroll.X,
                line.VisualTop + (line.Height - last.Height) + last.Baseline - textView.DefaultBaseline - scroll.Y)));
        }
        return marks;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var marks = Positions(textView);
        if (marks.Count == 0) return;
        var typeface = new Typeface(TextElement.GetFontFamily(textView), TextElement.GetFontStyle(textView),
            TextElement.GetFontWeight(textView), TextElement.GetFontStretch(textView));   // the editor's font, inherited
        var dpi = VisualTreeHelper.GetDpi(textView).PixelsPerDip;
        FormattedText Glyph(string s) => new(s, CultureInfo.InvariantCulture, textView.FlowDirection, typeface,
            TextElement.GetFontSize(textView), textView.NonPrintableCharacterBrush, dpi);
        FormattedText pilcrow = Glyph("¶"), arrow = Glyph("→"), dot = Glyph("·");
        foreach (var (glyph, at) in marks) drawingContext.DrawText(glyph switch { '¶' => pilcrow, '→' => arrow, _ => dot }, at);
    }

    /// <summary>How far the marks sit from the page toward the text when the theme's own
    /// mark colour isn't sent: fainter than the line numbers (the text at half strength),
    /// and at least 1.5:1 on every built-in theme, the floor <c>--mdm-mark</c> is held to.</summary>
    private const double Strength = 0.4;

    /// <summary>The marks' colour for the theme read-back JSON that ApplySourceColors
    /// receives: <see cref="BrushFor"/> of its colours and the <c>--mdm-mark</c> it sends.</summary>
    internal static SolidColorBrush ForReadBack(string? json) =>
        BrushFor(Themes.ThemeReadBack.Parse(json), SourcePalette.Parse(json)?.Mark);

    /// <summary>
    /// The marks' colour: the theme's own <c>--mdm-mark</c> when the page sent it, the same
    /// colour the formatted view's marks use; otherwise (a bundle from before it was sent)
    /// its text faded toward its page, so light grey on a light theme and a dim text colour
    /// on a dark one. A failed read-back (null) gets the default theme's <c>--mdm-mark</c>
    /// grey, for the original pane the source view then falls back to.
    /// </summary>
    internal static SolidColorBrush BrushFor((Color Background, Color Foreground)? read, Color? mark = null)
    {
        var colour = read is not { } r ? Color.FromRgb(0xC4, 0xC8, 0xD0)
            : mark ?? Color.FromRgb(Mix(r.Background.R, r.Foreground.R), Mix(r.Background.G, r.Foreground.G), Mix(r.Background.B, r.Foreground.B));
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;

        static byte Mix(byte page, byte text) => (byte)System.Math.Round(page + (text - page) * Strength);
    }
}
