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

    /// <summary>
    /// Each visible mark and where it goes in view coordinates (the glyph's top-left): →
    /// where a tab starts, · on the spaces <see cref="SpaceMarks"/> picks, ¶ right after the
    /// text of each line that ends in a line break, on its last wrapped row (the document's
    /// last line has none). Empty when off or before layout. Exposed so placement is
    /// testable without a DrawingContext.
    /// </summary>
    internal IReadOnlyList<(char Glyph, Point At)> Positions(TextView textView)
    {
        var marks = new List<(char, Point)>();
        if (!_enabled || !textView.VisualLinesValid || textView.Document is not { } doc) return marks;
        var scroll = textView.ScrollOffset;
        foreach (var line in textView.VisualLines)
        {
            var start = line.FirstDocumentLine.Offset;
            var end = line.LastDocumentLine.EndOffset;
            Point At(int offset) => line.GetVisualPosition(line.GetVisualColumn(offset - start), VisualYPosition.TextTop) - scroll;
            for (var tab = doc.IndexOf('\t', start, end - start); tab >= 0; tab = doc.IndexOf('\t', tab + 1, end - tab - 1))
                marks.Add(('→', At(tab)));
            var first = line.FirstDocumentLine;
            foreach (var space in SpaceMarks.Step(doc.GetText(first), StartOf(doc, first.LineNumber)).Marks)
                marks.Add(('·', At(start + space)));
            if (line.LastDocumentLine.DelimiterLength == 0) continue;
            var row = line.TextLines[^1];
            marks.Add(('¶', new Point(line.GetTextLineVisualXPosition(row, line.VisualLength),
                line.GetTextLineVisualYPosition(row, VisualYPosition.TextTop)) - scroll));
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

    /// <summary>How far the marks sit from the page toward the text: fainter than the line
    /// numbers (the text at half strength), and at least 1.5:1 on every built-in theme, the
    /// floor the formatted view's <c>--mdm-mark</c> is held to.</summary>
    private const double Strength = 0.4;

    /// <summary>
    /// The marks' colour for a theme read-back: its text faded toward its page, so light
    /// grey on a light theme and a dim text colour on a dark one. A failed read-back (null)
    /// gets the default theme's <c>--mdm-mark</c> grey, for the original pane the source
    /// view then falls back to.
    /// </summary>
    internal static SolidColorBrush BrushFor((Color Background, Color Foreground)? read)
    {
        var colour = read is { } r
            ? Color.FromRgb(Mix(r.Background.R, r.Foreground.R), Mix(r.Background.G, r.Foreground.G), Mix(r.Background.B, r.Foreground.B))
            : Color.FromRgb(0xC4, 0xC8, 0xD0);
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;

        static byte Mix(byte page, byte text) => (byte)System.Math.Round(page + (text - page) * Strength);
    }
}
