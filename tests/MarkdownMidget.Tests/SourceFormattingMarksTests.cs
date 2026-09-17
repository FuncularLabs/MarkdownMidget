using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using MarkdownMidget.Source;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The ¶ toggle in the source view (<see cref="SourceEditor.ShowMarks"/>), against a real
/// laid-out editor. The window wiring (toolbar button, theme changes) is MRK-01's human part.
/// </summary>
[Collection("WpfSta")]
public class SourceFormattingMarksTests
{
    private static T On<T>(Func<SourceEditor, T> body, string text)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            Window? win = null;
            try
            {
                var ed = new SourceEditor { FontFamily = new FontFamily("Consolas"), FontSize = 14, Text = text };
                win = new Window { Width = 400, Height = 400, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false, Content = ed };
                win.Show();
                result = body(ed);
            }
            catch (Exception ex) { error = ex; }
            finally { try { win?.Close(); } catch { } done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "marks harness timed out");
        if (error is not null) throw error;
        return result;
    }

    private static TextView Settle(SourceEditor ed)
    {
        ed.UpdateLayout();
        ed.TextArea.TextView.EnsureVisualLines();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        return ed.TextArea.TextView;
    }

    private static int GlyphsDrawn(SourceEditor ed)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) ed.Marks.Draw(Settle(ed), dc);
        return Count(visual.Drawing);
        static int Count(Drawing? d) => d is GlyphRunDrawing ? 1 : d is DrawingGroup g ? g.Children.Sum(Count) : 0;
    }

    private static Point PositionOf(SourceEditor ed, int offset) => ed.TextArea.TextView.GetVisualPosition(
        new TextViewPosition(ed.Document.GetLocation(offset)), VisualYPosition.TextTop) - ed.TextArea.TextView.ScrollOffset;

    private static Point[] Of(SourceEditor ed, char glyph) =>
        ed.Marks.Positions(Settle(ed)).Where(m => m.Glyph == glyph).Select(m => m.At).ToArray();

    [Fact]   // AC1
    public void TurningMarksOnDrawsOurMarksNotAvalonEditsAndOffHidesThemAll()
    {
        var states = On(ed =>
        {
            object Read()
            {
                var o = ed.TextArea.Options;   // AvalonEdit's own marks stay off: its line end draws "\n", its tab », its space · on every space
                return (o.ShowSpaces, o.ShowTabs, o.ShowEndOfLine, ed.ShowMarks, Of(ed, '¶').Length, Of(ed, '→').Length, Of(ed, '·').Length, GlyphsDrawn(ed));
            }
            var before = Read();
            ed.ShowMarks = true;
            var on = Read();
            ed.ShowMarks = false;
            return (before, on, Read());
        }, "a b\tc  \nsecond line\n");

        Assert.Equal((false, false, false, false, 0, 0, 0, 0), states.before);
        Assert.Equal((false, false, false, true, 2, 1, 2, 5), states.on);
        Assert.Equal(states.before, states.Item3);
    }

    [Theory]   // AC1
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EachLineEndingGetsOnePilcrowAfterItsTextWhateverTheNewline(string newline)
    {
        var (marks, ends) = On(ed =>
        {
            ed.ShowMarks = true;
            return (Of(ed, '¶'), Enumerable.Range(1, 3).Select(n => PositionOf(ed, ed.Document.GetLineByNumber(n).EndOffset)).ToArray());
        }, string.Join(newline, "a", "bbbb", "", "last line, no ending"));

        Assert.Equal(ends, marks);   // one per line ending, right after its text; none on the last line
        Assert.True(marks[1].X > marks[0].X && marks[2].X < marks[0].X, "marks follow the text's length");
    }

    [Fact]   // AC1
    public void EachTabAndLineEndIsMarkedOnTheWrappedRowItIsOn()
    {
        var text = "\tlead\n" + string.Concat(Enumerable.Repeat("word ", 30)) + "x\ty\t\nlast";
        var (marks, tabs, secondRowTop, pilcrow, lineEnd) = On(ed =>
        {
            ed.WordWrap = true;
            ed.ShowMarks = true;
            var starts = Enumerable.Range(0, text.Length).Where(i => text[i] == '\t').ToArray();
            return (Of(ed, '→'), starts.Select(i => PositionOf(ed, i)).ToArray(), PositionOf(ed, text.IndexOf("word", StringComparison.Ordinal)).Y,
                Of(ed, '¶')[1], PositionOf(ed, ed.Document.GetLineByNumber(2).EndOffset));
        }, text);

        Assert.Equal(3, tabs.Length);
        Assert.Equal(tabs, marks);
        Assert.True(marks[1].Y > secondRowTop, "the wrapped line's tabs are marked on the row they are on, not its first");
        Assert.Equal(lineEnd, pilcrow);   // and its ¶ on its last row
    }

    [Fact]   // spaces: the SpaceMarks rules, on screen, following edits above them
    public void SpacesAreDottedWhereTheRulesSayAndFollowAnEditAbove()
    {
        const string text = "a  b\n```\nc  d\n```\n";
        var (before, dotsBefore, after, dotsAfter) = On(ed =>
        {
            ed.ShowMarks = true;
            var dots = Of(ed, '·');   // laid out first, then asked where the spaces are
            var first = (new[] { 1, 2 }.Select(i => PositionOf(ed, i)).ToArray(), dots);
            ed.Document.Insert(0, "```\n");   // "a  b" is now code, and "c  d" is not
            dots = Of(ed, '·');
            var c = ed.Text.IndexOf("c  d", StringComparison.Ordinal);
            return (first.Item1, first.dots, new[] { c + 1, c + 2 }.Select(i => PositionOf(ed, i)).ToArray(), dots);
        }, text);

        Assert.Equal(before, dotsBefore);
        Assert.Equal(after, dotsAfter);
    }

    [Fact]   // review finding 4: a long line places only the marks in view
    public void OnlyTheMarksInViewArePlacedOnAVeryLongLine()
    {
        var text = string.Concat(Enumerable.Repeat("a\t  ", 2000)) + "end";   // 2,000 tabs and 4,000 dotted spaces on one line
        var (scrolled, inView, allInView, wrapped) = On(ed =>
        {
            ed.ShowMarks = true;
            Settle(ed);
            ed.ScrollToHorizontalOffset(20000);
            var view = Settle(ed);
            var marks = ed.Marks.Positions(view);
            var placed = (ed.Marks.Placed, marks.Count, marks.All(m => m.At.X > -40 && m.At.X < view.ActualWidth));
            ed.WordWrap = true;
            ed.Marks.Positions(Settle(ed));
            return (placed.Placed, placed.Count, placed.Item3, ed.Marks.Placed);
        }, text);

        Assert.InRange(inView, 1, 100);
        Assert.Equal(inView, scrolled);   // nothing placed off to either side
        Assert.True(allInView, "every mark returned is in the view");
        Assert.InRange(wrapped, 1, 1000);   // wrapped rows below the view are not placed either
    }

    [Fact]   // review round 2, N1: a pasted picture's 600 KB line walks its rows only down to the view's bottom
    public void AWrappedPictureSizedLineWalksOnlyTheRowsDownToTheViewsBottom()
    {
        var text = "![p](data:image/png;base64," + string.Concat(Enumerable.Repeat("iVBORw0KGgoAAAA\tNSUhEU  ", 25_000)) + ")\nafter";
        var (rows, atTop, inMiddle, marks, hits) = On(ed =>
        {
            ed.WordWrap = true;
            ed.ShowMarks = true;
            var view = Settle(ed);
            ed.Marks.Positions(view);
            var top = ed.Marks.RowsVisited;
            var line = view.VisualLines[0];
            ed.ScrollToVerticalOffset(line.Height / 2);
            view = Settle(ed);
            var placed = ed.Marks.Positions(view).Where(m => m.Glyph != '¶').ToArray();
            var onTheirCharacters = placed.Count(m => view.GetPosition(new Point(m.At.X + 1, m.At.Y + 2) + view.ScrollOffset) is { } p
                && ed.Document.GetCharAt(ed.Document.GetOffset(p.Location)) == (m.Glyph == '→' ? '\t' : ' '));
            return (line.TextLines.Count, top, ed.Marks.RowsVisited, placed.Length, onTheirCharacters);
        }, text);

        Assert.True(rows > 4000, $"only {rows} rows");
        Assert.InRange(atTop, 1, 40);                  // a 400-pixel view's rows, and the first below it
        Assert.InRange(inMiddle, rows / 2, rows / 2 + 40);   // the rows above, walked once, then the view's
        Assert.True(marks > 0, "marks in view");
        Assert.Equal(marks, hits);                     // each mark sits on its own tab or space
    }

    [Fact]   // review finding 3: MRK-02 step 2 against the editor, one undo step per keystroke
    public void TypedBackticksAndEnterUndoOneKeystrokeAtATime()
    {
        const string text = "a  b\n- item\nc  d\n";
        var (atStart, typed, afterOne, afterFour, restored) = On(ed =>
        {
            ed.ShowMarks = true;
            var start = Of(ed, '·').Length;
            ed.CaretIndex = text.IndexOf("- item", StringComparison.Ordinal);
            foreach (var key in new[] { "`", "`", "`", "\n" }) ed.TextArea.PerformTextInput(key);
            var dots = Of(ed, '·').Length;
            ed.Undo();
            var one = Of(ed, '·').Length;
            ed.Undo(); ed.Undo(); ed.Undo();
            return (start, dots, one, Of(ed, '·').Length, ed.Text == text);
        }, text);

        Assert.Equal((4, 2), (atStart, typed));   // the fence makes "c  d" code
        Assert.Equal(typed, afterOne);            // one Ctrl+Z takes back only the Enter: "```- item" still opens a fence
        Assert.Equal((atStart, true), (afterFour, restored));
    }

    [Fact]   // AC1
    public void LinesScrolledOutOfViewAreNotMarked()
    {
        var (marks, visible, bottom) = On(ed =>
        {
            ed.ShowMarks = true;
            ed.ScrollToLine(250);
            var view = Settle(ed);
            return (Of(ed, '¶'), view.VisualLines.Count, view.ActualHeight);
        }, string.Join("\n", Enumerable.Range(0, 300).Select(i => "line" + i)));

        Assert.InRange(marks.Length, 1, visible);
        Assert.All(marks, p => Assert.InRange(p.Y, -20, bottom));   // in the viewport, not at line 250's height in the document
    }

    [Fact]   // AC2
    public void MarksStayOnThroughANewDocumentAHiddenPaneAndReadOnly()
    {
        var after = On(ed =>
        {
            ed.ShowMarks = true;
            ed.Visibility = Visibility.Collapsed;   // the formatted view is showing
            ed.Text = "opened\nwhile\thidden\n";     // a document opened, or the view switched back
            ed.IsReadOnly = true;                     // Help, or Edit ▸ Read Only
            ed.Visibility = Visibility.Visible;
            return (ed.ShowMarks, Of(ed, '¶').Length, Of(ed, '→').Length);
        }, "first document");

        Assert.Equal((true, 2, 1), after);
    }

    private static string ReadBack(string bg, string fg)
    {
        static string Rgb(string hex) { var c = (Color)ColorConverter.ConvertFromString(hex); return $"{{\"r\":{c.R},\"g\":{c.G},\"b\":{c.B}}}"; }
        return $"{{\"background\":{Rgb(bg)},\"foreground\":{Rgb(fg)}}}";
    }

    [Theory]   // AC3
    [InlineData("#ffffff", "#1a1a1a", true)]    // Default
    [InlineData("#fafafa", "#383a42", true)]    // One Light
    [InlineData("#fdf6e3", "#657b83", true)]    // Solarized Light: the faintest text of the built-ins
    [InlineData("#282a36", "#f8f8f2", false)]   // Dracula
    [InlineData("#22272e", "#adbac7", false)]   // GitHub Dark Dimmed
    [InlineData("#293134", "#e0e2e4", false)]   // Obsidiminutive
    public void TheMarkColourIsTheThemeTextFadedTowardItsPage(string bg, string fg, bool light)
    {
        var (page, text) = ThemeReadBack.Parse(ReadBack(bg, fg))!.Value;
        var brush = FormattingMarks.BrushFor((page, text));
        var mark = brush.Color;
        static double Luminance(Color c) => SourcePalette.ContrastRatio(c, Colors.Black);
        var lineNumbers = Color.FromRgb((byte)((page.R + text.R) / 2), (byte)((page.G + text.G) / 2), (byte)((page.B + text.B) / 2));
        var contrast = SourcePalette.ContrastRatio(mark, page);

        Assert.True(brush.IsFrozen);
        Assert.True(contrast >= 1.5, $"mark {mark} is {contrast:F2}:1 on {page}, under the 1.5:1 --mdm-mark floor");
        Assert.True(contrast < SourcePalette.ContrastRatio(lineNumbers, page), $"mark {mark} is not fainter than the line numbers");
        Assert.True(light ? Luminance(mark) > Luminance(text) : Luminance(mark) < Luminance(text),
            $"mark {mark} is not {(light ? "lighter" : "dimmer")} than the text");
    }

    [Theory]   // review finding 6: the read-back as ApplySourceColors hands it over
    [InlineData(",\"mark\":{\"r\":98,\"g\":114,\"b\":164}", "#6272A4")]   // Dracula's --mdm-mark, as the formatted view shows it
    [InlineData("", "#7B7C81")]                                            // an older bundle: the text faded toward the page
    [InlineData(",\"mark\":null", "#7B7C81")]
    public void TheThemesOwnMarkColourWinsOverTheMix(string mark, string expected)
    {
        var json = "{\"background\":{\"r\":40,\"g\":42,\"b\":54},\"foreground\":{\"r\":248,\"g\":248,\"b\":242},\"source\":{"
            + "\"heading\":{\"r\":189,\"g\":147,\"b\":249},\"link\":{\"r\":139,\"g\":233,\"b\":253},\"accent\":{\"r\":80,\"g\":250,\"b\":123},"
            + "\"quote\":{\"r\":98,\"g\":114,\"b\":164}" + mark + "}}";
        var brush = FormattingMarks.ForReadBack(json);
        Assert.Equal((Color)ColorConverter.ConvertFromString(expected), brush.Color);
        Assert.True(brush.IsFrozen);
        Assert.Equal(Color.FromRgb(0xC4, 0xC8, 0xD0), FormattingMarks.ForReadBack("null").Color);   // no read-back at all
    }

    [Theory]   // AC3
    [InlineData(null)]
    [InlineData("null")]                  // the script threw
    [InlineData("{\"background\":{}}")]   // half a read-back
    public void AFailedThemeReadBackGivesTheDefaultThemesMarkGrey(string? json)
    {
        var brush = FormattingMarks.BrushFor(ThemeReadBack.Parse(json));
        Assert.Equal(Color.FromRgb(0xC4, 0xC8, 0xD0), brush.Color);   // theme-default.css --mdm-mark
        Assert.True(brush.IsFrozen);
    }

    [Fact]   // AC3
    public void TheMarksBrushIsTheTextViewsNonPrintableCharacterBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x62, 0x72, 0xA4));   // not AvalonEdit's default LightGray
        Assert.True(On(ed =>
        {
            ed.MarksBrush = brush;
            return ReferenceEquals(ed.TextArea.TextView.NonPrintableCharacterBrush, brush) && ReferenceEquals(ed.MarksBrush, brush);
        }, "x"));
    }

    [Fact]   // AC4
    public void MarksLeaveTheTextSavedBytesFindMatchesCopiedTextWordMovesCaretColumnAndLinesUnchanged()
    {
        const string text = "# Title\n\nTwo  spaces make a break  \nnext\tline with a tab\n   \n- item\n";
        var (off, on) = On(ed =>
        {
            string Snapshot()
            {
                Settle(ed);
                ed.SelectAll();
                var copied = ed.TextArea.Selection.GetText();   // what Copy puts on the clipboard
                ed.CaretIndex = 0;
                var words = string.Join(",", Enumerable.Range(0, 12).Select(_ => { System.Windows.Documents.EditingCommands.MoveRightByWord.Execute(null, ed.TextArea); return ed.CaretIndex; }));   // Ctrl+Right
                Assert.True(words.Split(',').Distinct().Count() >= 8, "the word moves moved: " + words);
                ed.CaretIndex = text.IndexOf("line", StringComparison.Ordinal);   // just after the tab
                var tabs = FindEngine.Build("\\t", FindEngine.Mode.Extended, matchCase: false, wholeWord: false)!;
                var spaces = FindEngine.Build(" ", FindEngine.Mode.Normal, matchCase: false, wholeWord: false)!;
                return string.Join("|", ed.Text, Convert.ToHexString(DocumentText.Encode(ed.Text, LineEnding.CrLf, bom: false)),
                    string.Join(",", tabs.Matches(ed.Text).Select(m => m.Index)), string.Join(",", spaces.Matches(ed.Text).Select(m => m.Index)),
                    copied, words, ed.CaretLineColumn(), ed.LineCount, ed.GetLineText(2), ed.GetCharacterIndexFromLineIndex(3));
            }
            var before = Snapshot();
            ed.ShowMarks = true;
            return (before, Snapshot());
        }, text);

        Assert.Equal(off, on);
        Assert.StartsWith(text + "|", on);   // the snapshot holds the real text
    }
}
