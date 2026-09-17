using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace MarkdownMidget.Source;

/// <summary>
/// The raw-markdown source view (Ctrl+E), backed by AvalonEdit.
///
/// It exists so the rest of the app does not have to learn AvalonEdit. The source
/// view began life as a WPF <see cref="TextBox"/>, and roughly seventy call sites
/// across MainWindow and SourceFormat speak its dialect: 0-based line indices,
/// character indices rather than document offsets, <c>CaretIndex</c>,
/// <c>TextWrapping</c>, <c>CaretBrush</c>, and geometry that is relative to the
/// control. A <see cref="TextBox"/> cannot colour a run of text, which is the whole
/// reason for the move — but nothing else about how those call sites work needed to
/// change, so every AvalonEdit-ism is confined here and translated back to the
/// TextBox-shaped surface they already expect.
///
/// Line-number convention, stated once because it is the easy thing to get wrong:
/// AvalonEdit counts document lines from 1; the TextBox API this replaces counts
/// display lines from 0. Every method here takes and returns the 0-based form and
/// converts at the boundary, except the status bar's CaretLineColumn.
/// </summary>
public class SourceEditor : TextEditor
{
    public SourceEditor()
    {
        // The app runs its own spell engine and draws its own squiggles; AvalonEdit
        // has no native checker, so there is nothing to turn off, but the caret and
        // wrapping defaults are set to match the old TextBox.
        WordWrap = false;
        Options.EnableHyperlinks = false;          // a URL in the markdown is text, not a link
        Options.EnableEmailHyperlinks = false;
        Options.CutCopyWholeLine = false;          // TextBox copies the selection, nothing more

        // Before the Document.Changed subscription below, whose handler tells it about edits.
        Marks = new FormattingMarks(TextArea.TextView);
        AddBackgroundRenderer(Marks);

        // Document.Changed carries real offsets (Offset / InsertionLength /
        // RemovalLength), which is exactly what the squiggle tracker needs and what
        // WPF's TextChangedEventArgs.Changes used to supply. Raised as TextEdited so
        // the host can keep shifting squiggle ranges through edits; the formatting
        // marks hear of it too, to re-read the lines from the edit down.
        //
        // Subscribed once, to the document that exists now. AvalonEdit's Text setter
        // mutates this document in place rather than replacing it, and the app never
        // assigns a new Document, so this subscription follows every edit. (If a future
        // change does reassign Document, it must re-wire this.)
        Document.Changed += OnDocumentChanged;

        // An image on the clipboard never reaches AvalonEdit's paste. Its Paste
        // command is enabled only while the clipboard holds text (CanPaste checks
        // Clipboard.ContainsText), and WPF re-runs that gate before executing a
        // command binding, so the DataObject.Pasting event is never raised for a
        // picture — there is nothing downstream to hook. The hook therefore sits on
        // the command itself, in the tunnelling phase, on this editor: the TextArea
        // that owns the command lives inside its template, so every way in (Ctrl+V,
        // Shift+Insert, Edit ▸ Paste) executes through here before the TextArea's
        // own binding gets a say. The key gestures also ask CanExecute before they
        // execute, and the TextArea would answer "no" for a picture, so the first
        // handler answers "yes" for them; Edit ▸ Paste (TextEditor.Paste) executes
        // directly and never asks. A paste that is not a picture is left unhandled
        // and takes AvalonEdit's path untouched.
        CommandManager.AddPreviewCanExecuteHandler(this, OnPreviewCanPaste);
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewPaste);
    }

    /// <summary>
    /// Raised for each document edit with TextBox-style offsets: where the change
    /// started, how many characters went in, how many came out. The squiggle adorner
    /// consumes this to keep underlines glued to their words between spell passes.
    /// </summary>
    public event Action<int, int, int>? TextEdited;

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        Marks.TextChanged(Document.GetLineByOffset(e.Offset).LineNumber);   // the space marks' context, from the edited line on
        TextEdited?.Invoke(e.Offset, e.InsertionLength, e.RemovalLength);
    }

    // ===== pasting a picture =====

    /// <summary>
    /// Where the paste handlers read the clipboard. The app leaves this on the real
    /// clipboard; tests point it at an in-memory DataObject so the Paste command can
    /// be driven end to end without touching the clipboard. Null means "nothing to
    /// look at", which is also the answer when another process has the clipboard
    /// locked — the same ExternalException AvalonEdit swallows in its own paste.
    /// </summary>
    internal Func<IDataObject?> ClipboardSource { get; set; } = ReadClipboard;

    /// <summary>Raised when a paste was refused for its size, carrying the notice to
    /// show: this editor has no status bar of its own, and the window has the one it
    /// shows for every other route (MainWindow.InitSource).</summary>
    internal event Action<string>? PictureRefused;

    /// <summary>The picture ceiling a paste here is held to. A seam, like
    /// <see cref="ClipboardSource"/>: the app leaves it at the shared ceiling, and a
    /// test lowers it rather than building 64 MB of PNG.</summary>
    internal long PictureCeiling { get; set; } = PictureLimit.MaxBytes;

    private static IDataObject? ReadClipboard()
    {
        try { return Clipboard.GetDataObject(); }
        catch (ExternalException) { return null; }
    }

    private void OnPreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        var data = ClipboardSource();
        if (data is null || !WantsImage(data)) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void OnPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        var data = ClipboardSource();
        if (data is not null && TryPasteImage(data)) e.Handled = true;
    }

    /// <summary>
    /// True when this paste is ours: the editor can take an insertion at the caret —
    /// read-only mode wins here exactly as it does for text, being the same gate
    /// AvalonEdit's CanPaste applies — and the data is a picture without text. A
    /// picture is a Bitmap or a registered PNG (<see cref="ImagePaste.PngFormat"/>),
    /// either one alone: the OS synthesises CF_BITMAP from a CF_DIB, so a DIB-only
    /// clipboard reads as a Bitmap too, and a clipboard holding only a PNG is a
    /// picture all the same.
    /// </summary>
    private bool WantsImage(IDataObject data) =>
        TextArea.ReadOnlySectionProvider.CanInsert(TextArea.Caret.Offset)
        && ImagePaste.ShouldHandle(
            hasText: data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text),
            hasImage: data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(ImagePaste.PngFormat));

    /// <summary>
    /// Paste <paramref name="data"/> as a picture if it is one. The clipboard's own
    /// PNG goes in as it is when it offers a usable one (<see cref="ImagePaste.UsablePng"/>);
    /// otherwise the bitmap is encoded as PNG (<see cref="ImagePaste.EncodePng"/>).
    /// The picture's markdown replaces the selection (or goes in at the caret) as a
    /// single undo step, the caret landing after it, as a text paste would. Returns
    /// false — and touches nothing — when the data carries text, has no picture to
    /// use, or the editor is read-only, so the caller can let the ordinary paste
    /// proceed.
    /// </summary>
    internal bool TryPasteImage(IDataObject data)
    {
        if (!WantsImage(data)) return false;
        var png = ImagePaste.UsablePng(ReadPng(data));
        if (png is null)
        {
            if (data.GetData(DataFormats.Bitmap, autoConvert: true) is not BitmapSource image) return false;
            png = ImagePaste.EncodePng(image);
        }
        // The one picture ceiling, on the bytes that would go in (PictureLimit).
        // Handled either way: the paste is ours, and a refused one inserts nothing
        // rather than falling back to AvalonEdit's text paste — which has no text to
        // paste here, so the refusal would land as silence.
        if (ImagePaste.RefusalFor(png, PictureCeiling) is { } refusal)
        {
            PictureRefused?.Invoke(refusal);
            return true;
        }
        var md = ImagePaste.MarkdownFor(png);
        // The selection's own replace runs inside one document update, so the
        // removal and the insertion undo together; an empty selection is one insert.
        TextArea.Selection.ReplaceSelectionWithText(md);
        TextArea.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// What <paramref name="data"/> holds under <see cref="ImagePaste.PngFormat"/>, or
    /// null. On the real route a failed read does not throw: the clipboard's data is a
    /// WPF DataObject over another program's OLE data object, and WPF's converter
    /// returns null for a "PNG" read that fails, so the fallback runs through
    /// <see cref="ImagePaste.UsablePng"/>(null) and the bitmap, when there is one,
    /// still pastes (SourceEditorTests.AFailingClipboardPngReadThroughOleFallsBackToTheBitmap
    /// pins that for CLIPBRD_E_BAD_DATA). The catch is a second line of defence, for a
    /// data object that throws from GetData itself. Either way the PNG is the
    /// preferred copy of the picture, not the only one.
    /// </summary>
    private static object? ReadPng(IDataObject data)
    {
        try { return data.GetData(ImagePaste.PngFormat); }
        catch (ExternalException) { return null; }
    }

    // ===== TextBox property shims =====

    /// <summary>The caret position as a character index — TextBox's <c>CaretIndex</c>,
    /// which AvalonEdit spells <see cref="TextEditor.CaretOffset"/>.</summary>
    public int CaretIndex
    {
        get => CaretOffset;
        set => CaretOffset = Math.Clamp(value, 0, Document?.TextLength ?? 0);
    }

    /// <summary>
    /// TextBox's <c>TextWrapping</c>, mapped to AvalonEdit's boolean <c>WordWrap</c>.
    /// Only Wrap vs. NoWrap is meaningful here (the source view has never used
    /// WrapWithOverflow), so anything that is not <see cref="TextWrapping.NoWrap"/>
    /// counts as wrapping — the same two-way switch the toolbar button drives.
    /// </summary>
    public TextWrapping TextWrapping
    {
        get => WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        set => WordWrap = value != TextWrapping.NoWrap;
    }

    /// <summary>
    /// Selection start, with TextBox semantics: setting it places the caret there and
    /// clears any existing selection. AvalonEdit's own setter keeps the current
    /// selection length, so after <c>SelectedText = "…"</c> — which leaves the
    /// inserted text selected — assigning a new start with the stale length in place
    /// throws "start + length past the end". Collapsing to a caret is what
    /// <see cref="SourceFormat"/> means every time it assigns this, and a following
    /// <see cref="SelectionLength"/> assignment re-selects from here.
    /// </summary>
    public new int SelectionStart
    {
        get => base.SelectionStart;
        set => Select(Math.Clamp(value, 0, Document?.TextLength ?? 0), 0);
    }

    /// <summary>Selection length, clamped to what remains from the current start so it
    /// can never name a range past the end (TextBox clamps; AvalonEdit throws).</summary>
    public new int SelectionLength
    {
        get => base.SelectionLength;
        set
        {
            var start = base.SelectionStart;
            var max = (Document?.TextLength ?? 0) - start;
            Select(start, Math.Clamp(value, 0, Math.Max(0, max)));
        }
    }

    /// <summary>The caret brush — TextBox exposes it directly; AvalonEdit parks it on
    /// the <see cref="TextArea"/>'s caret. Without setting it, a dark theme leaves the
    /// caret WPF-default black and it vanishes.</summary>
    public Brush CaretBrush
    {
        get => TextArea.Caret.CaretBrush;
        set => TextArea.Caret.CaretBrush = value;
    }

    // ===== formatting marks (the ¶ toggle) =====

    /// <summary>The ¶, → and · marks; drawn only while <see cref="ShowMarks"/> is on.</summary>
    internal FormattingMarks Marks { get; }

    /// <summary>
    /// The ¶ toolbar toggle, in this view: ¶ at each line ending, → at each tab and · on
    /// the spaces <see cref="SpaceMarks"/> picks (<see cref="Marks"/>). This setter
    /// is the one place the source view's marks are switched on and off. They are only
    /// painted, so the text and everything read from it is unchanged, and the editor lives
    /// as long as its window, so the setting holds through view switches and every document.
    /// </summary>
    public bool ShowMarks
    {
        get => Marks.Enabled;
        set => Marks.Enabled = value;
    }

    /// <summary>The marks' colour: AvalonEdit's <see cref="TextView.NonPrintableCharacterBrush"/>,
    /// which <see cref="Marks"/> draws with (and AvalonEdit's own marks would use).</summary>
    public Brush MarksBrush
    {
        get => TextArea.TextView.NonPrintableCharacterBrush;
        set => TextArea.TextView.NonPrintableCharacterBrush = value;
    }

    // ===== line / offset mapping (0-based, TextBox semantics) =====

    /// <summary>Number of document lines, or 0 when there is no document. TextBox
    /// returns -1 before layout; AvalonEdit always knows its line count, so callers
    /// that guarded <c>&lt;= 0</c> simply never trip that guard now.</summary>
    public new int LineCount => Document?.LineCount ?? 0;

    /// <summary>The 0-based line a character index sits on.</summary>
    public int GetLineIndexFromCharacterIndex(int charIndex)
    {
        var doc = Document;
        if (doc is null) return -1;
        if (charIndex < 0 || charIndex > doc.TextLength) return -1;
        return doc.GetLineByOffset(charIndex).LineNumber - 1;
    }

    /// <summary>The character index at the start of a 0-based line.</summary>
    public int GetCharacterIndexFromLineIndex(int lineIndex)
    {
        var doc = Document;
        if (doc is null) return -1;
        if (lineIndex < 0 || lineIndex >= doc.LineCount) return -1;
        return doc.GetLineByNumber(lineIndex + 1).Offset;
    }

    /// <summary>The text of a 0-based line, without its terminator. Empty string for a
    /// line index out of range, matching TextBox's forgiving <c>GetLineText</c>.</summary>
    public string GetLineText(int lineIndex)
    {
        var doc = Document;
        if (doc is null || lineIndex < 0 || lineIndex >= doc.LineCount) return string.Empty;
        var line = doc.GetLineByNumber(lineIndex + 1);
        return doc.GetText(line.Offset, line.Length);
    }

    /// <summary>The first 0-based DOCUMENT line currently visible, or -1 before layout.
    /// This is a document line, not a display line: with word wrap on the two differ,
    /// and the scroll-anchor logic wants the document line.</summary>
    public int GetFirstVisibleLineIndex()
    {
        var tv = TextArea.TextView;
        tv.EnsureVisualLines();
        var lines = tv.VisualLines;
        if (lines.Count == 0) return -1;
        return lines[0].FirstDocumentLine.LineNumber - 1;
    }

    /// <summary>The last 0-based document line currently visible, or -1 before layout.</summary>
    public int GetLastVisibleLineIndex()
    {
        var tv = TextArea.TextView;
        tv.EnsureVisualLines();
        var lines = tv.VisualLines;
        if (lines.Count == 0) return -1;
        return lines[^1].LastDocumentLine.LineNumber - 1;
    }

    /// <summary>
    /// Scroll a 0-based line into view. TextBox's <c>ScrollToLine</c> takes a display
    /// index and no-ops out of range; AvalonEdit's takes a 1-based line, so convert
    /// and clamp.
    /// </summary>
    public new void ScrollToLine(int lineIndex)
    {
        var count = LineCount;
        if (count <= 0) return;
        base.ScrollToLine(Math.Clamp(lineIndex, 0, count - 1) + 1);
    }

    // ===== the caret's line and column, for the status bar (#10) =====

    /// <summary>Raised whenever the caret moves.</summary>
    public event EventHandler CaretMoved
    {
        add => TextArea.Caret.PositionChanged += value;
        remove => TextArea.Caret.PositionChanged -= value;
    }

    /// <summary>The caret's line as the status bar shows it — 1-based, the one exception to
    /// this class's 0-based rule — and its column by <see cref="LineColumn.Column"/>.</summary>
    public (int Line, int Column) CaretLineColumn()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        return (line.LineNumber, LineColumn.Column(Document.GetText(line.Offset, CaretOffset - line.Offset)));
    }

    // ===== hit-testing and glyph geometry =====

    /// <summary>
    /// The character index nearest a point, in control coordinates.
    ///
    /// TextBox with <paramref name="snapToText"/> true always returns an index; a
    /// click in the empty area below the last line snaps to the end. AvalonEdit's
    /// <see cref="TextEditor.GetPositionFromPoint"/> returns null there instead, and
    /// the spell context menu relies on getting an index — so a null with snapping
    /// requested falls back to the end offset. Without snapping, null passes through
    /// as -1 (nothing was under the pointer).
    /// </summary>
    public int GetCharacterIndexFromPoint(Point point, bool snapToText)
    {
        var doc = Document;
        if (doc is null) return -1;
        // GetPositionFromPoint expects a point relative to the text view, which sits
        // inside the control at the scroll offset.
        var tv = TextArea.TextView;
        tv.EnsureVisualLines();
        var docPoint = new Point(point.X + tv.HorizontalOffset, point.Y + tv.VerticalOffset);
        var pos = TextArea.TextView.GetPosition(docPoint);
        if (pos is { } p) return doc.GetOffset(p.Location);
        return snapToText ? doc.TextLength : -1;
    }

    // ===== Replace All (#5) =====

    /// <summary>
    /// Applies planned replacements as ONE undo unit: the document is put into an
    /// update (<see cref="TextDocument.BeginUpdate"/>, which opens an undo group
    /// that <see cref="TextDocument.EndUpdate"/> closes), and the edits go in last
    /// to first so each index — planned against the text as it was — is still
    /// right when its turn comes. Per-edit replacement rather than one rewrite of
    /// the span keeps the untouched text untouched, so the squiggle tracker only
    /// shifts around the words that changed. Returns the number applied. An empty
    /// plan touches nothing and leaves no undo entry behind.
    /// </summary>
    public int ApplyEdits(IReadOnlyList<FindEngine.Edit> edits)
    {
        if (edits.Count == 0) return 0;
        var doc = Document;
        doc.BeginUpdate();
        try
        {
            for (var i = edits.Count - 1; i >= 0; i--)
            {
                var e = edits[i];
                doc.Replace(e.Index, e.Length, e.Text);
            }
        }
        finally { doc.EndUpdate(); }
        return edits.Count;
    }

    // ===== the background-renderer hook the squiggles draw through =====

    /// <summary>Add a background renderer to the text view — the AvalonEdit-native
    /// way to draw under the text, used by highlighting. Exposed so callers need not
    /// reach through <see cref="TextArea"/>.</summary>
    public void AddBackgroundRenderer(IBackgroundRenderer renderer) =>
        TextArea.TextView.BackgroundRenderers.Add(renderer);
}
