using System.Text;

namespace MarkdownMidget;

/// <summary>
/// Line numbers and spell check for a large document, which start off for it: over <see cref="Bytes"/> of UTF-8 when it loads, or from the moment a
/// load or a view switch of it takes longer than <see cref="Slow"/>. On a 2.8 MB table-heavy file one spell check takes about 7 s, and the formatted
/// view's margin adds about 1.6 s to reading the document out of the editor, where that read alone takes under 1 s. Nothing here changes the saved
/// settings: while a document is in this state a click on either one is that document's own choice, kept until another document is loaded or it is
/// closed (a reload of the same one keeps it, and so does a Save As that keeps it under a new name). A document that is not large follows the saved settings.
/// </summary>
internal sealed class LargeDocument
{
    public const int Bytes = 512 * 1024;
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(5);
    public const string Note = "Large document: line numbers and spell check are off. Turn them on from View ▸ Line Numbers ▸ Show Line Numbers and View ▸ Spell Check.";

    private bool _off, _noted, _spell, _documentLines, _sourceLines;

    /// <summary>A document was loaded; <paramref name="sameDocument"/> when it is the one already open, reloaded or kept under a new name. True: show <see cref="Note"/> now.</summary>
    public bool Loaded(string text, bool sameDocument)
    {
        if (!sameDocument) Closed();
        _off |= Encoding.UTF8.GetByteCount(text) > Bytes;
        return ShouldNote();
    }

    /// <summary>The document was closed: no document is large, so the no-document screen follows the saved settings.</summary>
    public void Closed() => _off = _noted = _spell = _documentLines = _sourceLines = false;

    /// <summary>A load or a view switch of this document took <paramref name="elapsed"/>. True: show <see cref="Note"/> now.</summary>
    public bool Took(TimeSpan elapsed) { _off |= elapsed > Slow; return ShouldNote(); }

    public bool SpellCheck(bool saved) => _off ? _spell : saved;
    public bool LineNumbers(bool saved, bool sourceView) => _off ? (sourceView ? _sourceLines : _documentLines) : saved;

    /// <summary>The user turned spell check <paramref name="on"/> or off. True: that was this document's own choice, and the saved setting stays as it is.</summary>
    public bool ChooseSpell(bool on) { if (_off) _spell = on; return _off; }

    /// <summary>As <see cref="ChooseSpell"/>, for the line numbers of the views named (both, while View ▸ Line Numbers ▸ Same Setting for Both Views is on).</summary>
    public bool ChooseLines(bool on, bool document, bool source)
    {
        if (_off) { if (document) _documentLines = on; if (source) _sourceLines = on; }
        return _off;
    }

    private bool ShouldNote() => _off && !_noted && (_noted = true);
}
