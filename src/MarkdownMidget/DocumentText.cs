using System.IO;
using System.Text;

namespace MarkdownMidget;

/// <summary>The line-ending convention a file uses, remembered so Save can put it back.
/// Public, like ExternalChangeChoice and FindEngine.Mode, so a public test method can take one.</summary>
public enum LineEnding { Lf, CrLf, Cr }

/// <summary>
/// The bytes of a markdown file, in and out (issue #3).
///
/// Two things about a file are not part of the document and yet must survive a
/// round trip through the editor: its line-ending convention and its UTF-8
/// byte-order mark. Neither is something the editor should ever see. The
/// formatted view's serialiser emits LF between blocks but keeps whatever it
/// found INSIDE fenced, indented and HTML blocks, so a CRLF file with one code
/// block used to save with mixed endings; and the save path wrote UTF-8 without
/// a mark, so a file that had one lost it.
///
/// The rule is therefore: everything in memory is LF. <see cref="Detect"/> reads
/// the bytes, records the convention and the mark, and hands back text with every
/// <c>\r\n</c>, bare <c>\r</c> and <c>\n</c> folded to <c>\n</c>; both views see
/// the same text, and dirty tracking compares like with like. <see cref="Encode"/>
/// folds AGAIN before re-applying the convention - the serialiser can hand back
/// mixed endings, and a naive <c>Replace("\n", "\r\n")</c> on those would put
/// <c>\r\r\n</c> inside every code block - then puts the mark in front iff the
/// file had one.
/// </summary>
internal static class DocumentText
{
    /// <summary>What <see cref="Detect"/> found: the text, folded to LF, plus the
    /// two facts about the file that Save needs to reproduce it.</summary>
    internal readonly record struct Decoded(string Text, LineEnding Ending, bool HadBom);

    /// <summary>
    /// New documents are LF. Markdown's ecosystem writes LF (GitHub, static-site
    /// generators, git's own normalisation), and the formatted view's serialiser
    /// already emits LF, so it is the convention nobody is surprised by. The
    /// source view could disagree - AvalonEdit's Enter inserts the platform's
    /// CRLF into an empty document - which is one more reason Save folds first.
    /// </summary>
    public const LineEnding DefaultLineEnding = LineEnding.Lf;

    /// <summary>An untitled document: nothing in it, no file to take a convention from.</summary>
    public static Decoded NewDocument => new(string.Empty, DefaultLineEnding, HadBom: false);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Decode a file. The same BOM-detecting decode the load path always used
    /// (UTF-8 unless a mark says otherwise), so a UTF-16 file still opens; but the
    /// only mark reported as "had one" is the UTF-8 mark, because it is the only
    /// one that can be put back in front of the UTF-8 bytes <see cref="Encode"/>
    /// writes. A UTF-16 file keeps being written back as UTF-8 without a mark, as
    /// it was before.
    /// </summary>
    public static Decoded Detect(byte[] bytes)
    {
        var hadBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Utf8Bom);
        using var reader = new StreamReader(new MemoryStream(bytes));   // detectEncodingFromByteOrderMarks
        var decoded = Detect(reader.ReadToEnd());
        return decoded with { HadBom = hadBom };
    }

    /// <summary>
    /// The string flavour, for text that arrives already decoded: the plaintext
    /// out of an encrypted container, or a file the browser read for a drop. There
    /// is no mark to find in a string (the decoder that produced it took it), so
    /// <see cref="Decoded.HadBom"/> is false.
    /// </summary>
    public static Decoded Detect(string text) => new(Fold(text), Classify(text), HadBom: false);

    /// <summary>
    /// The convention a file uses, by majority. A tie goes to CRLF when CRLF is
    /// part of it - the platform's own, on the only platform this app runs on -
    /// and to LF between LF and bare CR. A file with no endings at all gives no
    /// evidence either way, so it gets what a new document gets.
    /// </summary>
    private static LineEnding Classify(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++;
                    break;
                case '\r': cr++; break;
                case '\n': lf++; break;
            }
        }
        if (crlf == 0 && lf == 0 && cr == 0) return DefaultLineEnding;
        if (crlf >= lf && crlf >= cr) return LineEnding.CrLf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }

    /// <summary>Every <c>\r\n</c>, bare <c>\r</c> and <c>\n</c> becomes <c>\n</c>.
    /// One pass: <c>\r\r\n</c> is a bare CR followed by a CRLF, two endings, and a
    /// pair of Replace calls gets that right in one order only (CRLF first) - a
    /// trap the single loop cannot fall into.</summary>
    public static string Fold(string text)
    {
        if (text.IndexOf('\r') < 0) return text;   // the common case, and free
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append('\n');
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Fold, then re-apply <paramref name="ending"/> throughout. This is the text
    /// an encrypted save seals: the container takes a string, not bytes, and a
    /// byte-order mark inside ciphertext would be pointless, so the encrypted path
    /// uses this and never <see cref="Encode"/>.
    /// </summary>
    public static string ApplyLineEnding(string text, LineEnding ending)
    {
        var folded = Fold(text);
        return ending switch
        {
            LineEnding.CrLf => folded.Replace("\n", "\r\n", StringComparison.Ordinal),
            LineEnding.Cr => folded.Replace('\n', '\r'),
            _ => folded,
        };
    }

    /// <summary>
    /// The bytes to write: UTF-8, <paramref name="ending"/> throughout, the mark in
    /// front iff <paramref name="bom"/>. A text that already begins with U+FEFF
    /// (it shouldn't - Detect strips it - but a caller could hand one over) is not
    /// given a second mark.
    /// </summary>
    public static byte[] Encode(string text, LineEnding ending, bool bom)
    {
        var body = ApplyLineEnding(text, ending);
        if (bom && body.StartsWith('\uFEFF')) body = body[1..];
        var bytes = Encoding.UTF8.GetBytes(body);   // GetBytes never emits a preamble
        if (!bom) return bytes;
        var withBom = new byte[Utf8Bom.Length + bytes.Length];
        Utf8Bom.CopyTo(withBom, 0);
        bytes.CopyTo(withBom, Utf8Bom.Length);
        return withBom;
    }
}
