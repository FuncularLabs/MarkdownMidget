using System.Text;
using System.Text.RegularExpressions;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Line endings and the UTF-8 byte-order mark survive a load/save round trip
/// (issue #3, release-1.0 stage 0.12 R4 and R5). The hard case is the serialiser:
/// it emits LF between blocks but keeps whatever was INSIDE fenced, indented and
/// HTML blocks, so a CRLF document with one code block comes back mixed. Every
/// corpus here therefore has all three block kinds, each carrying the other
/// ending inside it, and the assertion is "one ending kind throughout".
/// </summary>
public class DocumentTextTests
{
    private const string Crlf = "\r\n";
    private const string Lf = "\n";
    private const string Cr = "\r";

    /// <summary>
    /// A document whose structure uses <paramref name="outer"/> and whose fenced,
    /// indented and HTML blocks carry <paramref name="inner"/> - the shape the
    /// serialiser hands back for a file it only half-normalised. Fifteen endings
    /// in total (eleven outer, four inner), so a fold that miscounts is visible as
    /// a changed count.
    /// </summary>
    private static string Corpus(string outer, string inner) =>
        "# Title" + outer +
        outer +
        "```" + outer +
        "code line 1" + inner +
        "code line 2" + outer +
        "```" + outer +
        outer +
        "    indented 1" + inner +
        "    indented 2" + outer +
        outer +
        "<div>" + inner +
        "  html" + inner +
        "</div>" + outer +
        outer +
        "para" + outer;

    private const int CorpusEndings = 15;

    private static int Count(string s, string needle)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static int Count(string s, char c) => s.Count(ch => ch == c);

    /// <summary>Endings in <paramref name="s"/> counted the way a reader sees them:
    /// a CRLF pair is one ending, not two.</summary>
    private static int Endings(string s) => Count(s, Crlf) + Count(s, Lf) + Count(s, Cr) - 2 * Count(s, Crlf);

    [Theory]
    [InlineData(Crlf, Lf, LineEnding.CrLf)]   // a CRLF file whose blocks were normalised to LF
    [InlineData(Lf, Crlf, LineEnding.Lf)]     // an LF file whose blocks were pasted from Windows
    [InlineData(Cr, Lf, LineEnding.Cr)]       // classic Mac, for completeness of the fold
    [InlineData(Crlf, Cr, LineEnding.CrLf)]   // bare CR inside, CRLF outside
    public void LineEndingsRoundTrip(string outer, string inner, LineEnding expected)
    {
        var bytes = Encoding.UTF8.GetBytes(Corpus(outer, inner));

        var loaded = DocumentText.Detect(bytes);

        Assert.Equal(expected, loaded.Ending);
        Assert.False(loaded.HadBom);
        Assert.DoesNotContain('\r', loaded.Text);                 // in memory it is always LF
        Assert.Equal(CorpusEndings, Count(loaded.Text, '\n'));    // and every ending is still there

        var saved = Encoding.UTF8.GetString(DocumentText.Encode(loaded.Text, loaded.Ending, loaded.HadBom));

        AssertOneEndingKind(saved, expected);
        Assert.Equal(CorpusEndings, Endings(saved));
        Assert.Equal(loaded.Text, DocumentText.Fold(saved));      // content unchanged by the trip
    }

    [Fact]
    public void MixedMajorityTakesTheMajority()
    {
        // Eleven CRLF outside, four LF inside, plus one stray LF line break outside
        // (twelve CRLF to five LF): the file is CRLF by any reasonable reading, and
        // it stays CRLF everywhere - including inside the blocks that were LF.
        var text = Corpus(Crlf, Lf) + "trailing" + Lf + "line" + Crlf;
        var loaded = DocumentText.Detect(Encoding.UTF8.GetBytes(text));
        Assert.Equal(LineEnding.CrLf, loaded.Ending);

        var saved = Encoding.UTF8.GetString(DocumentText.Encode(loaded.Text, loaded.Ending, loaded.HadBom));
        AssertOneEndingKind(saved, LineEnding.CrLf);
        Assert.Equal(CorpusEndings + 2, Endings(saved));

        // And the mirror image: LF outside, CRLF inside the blocks stays LF.
        var lfFile = DocumentText.Detect(Encoding.UTF8.GetBytes(Corpus(Lf, Crlf) + "x" + Crlf));
        Assert.Equal(LineEnding.Lf, lfFile.Ending);
        AssertOneEndingKind(
            Encoding.UTF8.GetString(DocumentText.Encode(lfFile.Text, lfFile.Ending, lfFile.HadBom)),
            LineEnding.Lf);
    }

    private static void AssertOneEndingKind(string saved, LineEnding kind)
    {
        var crlf = Count(saved, Crlf);
        var lf = Count(saved, '\n');
        var cr = Count(saved, '\r');
        Assert.True(lf + cr > 0, "the corpus has line endings; the output must too");
        switch (kind)
        {
            case LineEnding.CrLf:
                Assert.Equal(crlf, lf);   // every LF is the tail of a CRLF
                Assert.Equal(crlf, cr);   // every CR is the head of one - no \r\r\n
                break;
            case LineEnding.Lf:
                Assert.Equal(0, cr);
                break;
            case LineEnding.Cr:
                Assert.Equal(0, lf);
                break;
        }
    }

    [Fact]
    public void BomRoundTrip()
    {
        var bom = Encoding.UTF8.GetPreamble();
        var body = "hello" + Lf + "world" + Lf;

        // Present: detected, stripped from the text, written back once.
        var withBom = DocumentText.Detect([.. bom, .. Encoding.UTF8.GetBytes(body)]);
        Assert.True(withBom.HadBom);
        Assert.Equal(body, withBom.Text);   // no U+FEFF leaks into the document
        var savedWith = DocumentText.Encode(withBom.Text, withBom.Ending, withBom.HadBom);
        Assert.Equal(bom, savedWith[..3]);
        Assert.Equal(body, Encoding.UTF8.GetString(savedWith[3..]));

        // Absent: not detected, not added.
        var without = DocumentText.Detect(Encoding.UTF8.GetBytes(body));
        Assert.False(without.HadBom);
        var savedWithout = DocumentText.Encode(without.Text, without.Ending, without.HadBom);
        Assert.Equal(Encoding.UTF8.GetBytes(body), savedWithout);

        // Never twice: a text that already starts with the mark gets exactly one.
        var twice = DocumentText.Encode("\uFEFF" + body, LineEnding.Lf, bom: true);
        Assert.Equal(bom, twice[..3]);
        Assert.Equal(body, Encoding.UTF8.GetString(twice[3..]));
    }

    [Fact]
    public void NonUtf8BomIsDecodedButNotCarried()
    {
        // A UTF-16 file has always been read through the BOM-detecting decoder and
        // written back as UTF-8 without a mark. That is unchanged: the only mark
        // "kept as found" is the UTF-8 one, because it is the only one that can be
        // put back in front of the UTF-8 bytes the app writes.
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi" + Crlf)).ToArray();
        var loaded = DocumentText.Detect(utf16);
        Assert.Equal("hi" + Lf, loaded.Text);
        Assert.Equal(LineEnding.CrLf, loaded.Ending);
        Assert.False(loaded.HadBom);
    }

    [Fact]
    public void EncodeFoldsBeforeReapplying()
    {
        // What the serialiser hands back for a CRLF document with one fenced block:
        // LF between blocks, the original CRLF kept inside. A naive
        // Replace("\n", "\r\n") turns the inside into \r\r\n.
        var mixed = "para" + Lf + "```" + Lf + "code" + Crlf + "more" + Crlf + "```" + Lf;

        var saved = Encoding.UTF8.GetString(DocumentText.Encode(mixed, LineEnding.CrLf, bom: false));

        Assert.DoesNotContain("\r\r\n", saved);
        Assert.Equal(5, Count(saved, Crlf));
        Assert.Equal(5, Count(saved, '\r'));
        Assert.Equal(5, Count(saved, '\n'));
        Assert.Equal("para" + Crlf + "```" + Crlf + "code" + Crlf + "more" + Crlf + "```" + Crlf, saved);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\nd", LineEnding.CrLf)]     // 2 CRLF beat 1 LF
    [InlineData("a\nb\nc\r\nd", LineEnding.Lf)]         // 2 LF beat 1 CRLF
    [InlineData("a\rb\rc\nd", LineEnding.Cr)]           // 2 CR beat 1 LF
    [InlineData("a\r\nb\n", LineEnding.CrLf)]           // tie CRLF/LF: CRLF, the platform's own
    [InlineData("a\rb\r\n", LineEnding.CrLf)]           // tie CR/CRLF: CRLF
    [InlineData("a\rb\n", LineEnding.Lf)]               // tie CR/LF, no CRLF in it: LF, the less exotic
    [InlineData("a\r\nb\rc\n", LineEnding.CrLf)]        // three-way tie: CRLF
    [InlineData("one line, no ending", LineEnding.Lf)]  // nothing to go on: same as a new document
    [InlineData("", LineEnding.Lf)]
    public void DetectMajorityAndTies(string text, LineEnding expected)
    {
        Assert.Equal(expected, DocumentText.Detect(text).Ending);
        Assert.Equal(expected, DocumentText.Detect(Encoding.UTF8.GetBytes(text)).Ending);
    }

    [Fact]
    public void DetectFoldsEveryEndingToLf()
    {
        var loaded = DocumentText.Detect("a\r\nb\rc\nd\r\r\ne");
        // \r\r\n is a bare CR followed by a CRLF: two endings, not three and not one.
        Assert.Equal("a\nb\nc\nd\n\ne", loaded.Text);
        Assert.False(loaded.HadBom);   // a string has already been decoded; no mark to find
    }

    [Fact]
    public void FoldIsIdempotent()
    {
        var once = DocumentText.Fold("x\r\ny\rz\n");
        Assert.Equal("x\ny\nz\n", once);
        Assert.Equal(once, DocumentText.Fold(once));
    }

    [Theory]
    [InlineData(LineEnding.Lf)]
    [InlineData(LineEnding.CrLf)]
    [InlineData(LineEnding.Cr)]
    public void ApplyLineEndingIsTheTextEncodeWrites(LineEnding ending)
    {
        // The encrypted save path can't take bytes - the container seals a string -
        // so it applies the ending to the plaintext with this and skips the BOM. It
        // must agree with what the plain path writes, minus the mark.
        var mixed = "a" + Lf + "```" + Lf + "b" + Crlf + "c" + Cr + "```" + Lf;
        var applied = DocumentText.ApplyLineEnding(mixed, ending);
        Assert.Equal(applied, Encoding.UTF8.GetString(DocumentText.Encode(mixed, ending, bom: false)));
        AssertOneEndingKind(applied, ending);
        Assert.Equal(5, Endings(applied));
    }

    [Fact]
    public void AnEncryptedWriteKeepsTheLineEndingButNotTheMark()
    {
        // What an encrypted save does to a CRLF file that had a UTF-8 mark: the
        // plaintext it seals is ApplyLineEnding's, so what comes back out of the
        // container is CRLF throughout with no U+FEFF in front, and reads back as
        // a file without a mark. The CHANGELOG says so for issue #3; it used to
        // list "encrypt" among the routes that keep the mark.
        var bom = Encoding.UTF8.GetPreamble();
        var loaded = DocumentText.Detect([.. bom, .. Encoding.UTF8.GetBytes(Corpus(Crlf, Lf))]);
        Assert.True(loaded.HadBom);

        var sealedText = DocumentText.ApplyLineEnding(loaded.Text, loaded.Ending);
        var container = Secure.SecureMarkdownFormat.Encrypt(
            sealedText, "pw", Secure.SecureMarkdownFormat.KdfProfile.FastForTests);
        var opened = Secure.SecureMarkdownFormat.Decrypt(container, "pw");

        Assert.False(opened.StartsWith('\uFEFF'));
        AssertOneEndingKind(opened, LineEnding.CrLf);
        var reread = DocumentText.Detect(opened);
        Assert.Equal(LineEnding.CrLf, reread.Ending);
        Assert.False(reread.HadBom);
        Assert.Equal(loaded.Text, reread.Text);
    }

    [Fact]
    public void EveryRouteWritesTheConventionsTheChangelogSays()
    {
        // A wiring pin, read from the source: these are window handlers no test can
        // run, so this proves which route calls which function with which arguments,
        // and the tests above prove what those functions do. Each encrypted write
        // seals ApplyLineEnding(text, the file's ending); each unencrypted write hands
        // Encode the file's ending and its remembered mark.
        //
        // Every pin names the whole call, arguments included, and is looked for only
        // inside its own method. A method that only seals may not call Encode at all.
        // Save and Save My Version As seal one way and write plain the other, so each
        // may call Encode exactly once, in its pinned plain write, and nowhere else in
        // the method: not nested in the encrypted arm, not before the branch. Every
        // violation is reported, not just the first.
        //
        // What it does not check: which arm of an if a pinned call sits in; what
        // reaches a call's arguments (a variable reassigned between the capture and
        // the call, a mark prepended without Encode), because a scan proves a call is
        // there, not what it does; and anything on a line after a string literal that
        // holds "//", which Code strips as though it began a comment.
        var source = RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs");
        var violations = new List<string>();
        const string encode = "DocumentText.Encode(";

        string Method(string signature) => Code(RepoSources.MethodBody(source, signature));
        void Has(string where, string code, string call)
        {
            if (!code.Contains(Code(call), StringComparison.Ordinal)) violations.Add($"MISSING in {where}: {call}");
        }
        void Lacks(string where, string code, string call)
        {
            if (code.Contains(Code(call), StringComparison.Ordinal)) violations.Add($"FORBIDDEN in {where}: {call}");
        }
        // The plain write exactly once; with it taken out, no Encode left in the method.
        void OnlyEncodeIs(string where, string code, string plainWrite)
        {
            var call = Code(plainWrite);
            var found = Count(code, call);
            if (found != 1) violations.Add($"EXPECTED ONCE in {where}, found {found}: {plainWrite}");
            else code = code.Remove(code.IndexOf(call, StringComparison.Ordinal), call.Length);
            Lacks($"{where}, outside the plain write", code, encode);
        }

        // File ▸ Encrypt Document…
        var encrypt = Method("private async void Encrypt_Click(");
        Has("Encrypt_Click", encrypt, "var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);");
        Has("Encrypt_Click", encrypt, "Secure.SecureMarkdownFile.Save(target, plaintext, pw)");
        Lacks("Encrypt_Click", encrypt, encode);

        // File ▸ Change Password…
        var password = Method("private async void ChangePassword_Click(");
        Has("ChangePassword_Click", password, "var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);");
        Has("ChangePassword_Click", password, "Secure.SecureMarkdownFile.Save(_currentPath, plaintext, pw)");
        Lacks("ChangePassword_Click", password, encode);

        // File ▸ Convert to Unencrypted…
        Has("ConvertPlain_Click", Method("private async void ConvertPlain_Click("),
            "file.WriteAsync(DocumentText.Encode(markdown, _lineEnding, _hadBom))");

        // Save and Save As: sealed one way, written plain the other.
        var save = Method("private async Task<bool> SaveAsync(");
        Has("SaveAsync", save, "var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);");
        Has("SaveAsync", save, "Secure.SecureMarkdownFile.Save(path, plaintext, pw)");
        OnlyEncodeIs("SaveAsync", save, "file.WriteAsync(DocumentText.Encode(markdown, _lineEnding, _hadBom))");

        // The timestamped .bak an external change writes: the file's conventions
        // captured, then one writer each way, each passing them on.
        var external = Method("private async Task HandleExternalChangeAsync(");
        Has("HandleExternalChangeAsync", external, "var (ending, bom) = (_lineEnding, _hadBom);");
        Has("HandleExternalChangeAsync", external, "WriteTimestampedEncryptedBackup(path, inMemory, ending, bakPw)");
        Has("HandleExternalChangeAsync", external, "WriteTimestampedBackup(path, inMemory, ending, bom)");
        Has("WriteTimestampedBackup", Method("private static string WriteTimestampedBackup("),
            "File.WriteAllBytes(path, DocumentText.Encode(content, ending, bom))");
        var sealedBak = Method("private static string WriteTimestampedEncryptedBackup(");
        Has("WriteTimestampedEncryptedBackup", sealedBak,
            "Secure.SecureMarkdownFormat.Encrypt(DocumentText.ApplyLineEnding(content, ending), password)");
        Lacks("WriteTimestampedEncryptedBackup", sealedBak, encode);

        // Save My Version As… on the external-change prompt: the same two ways.
        var mine = Method("private async Task HandleSaveAsAfterExternalChangeAsync(");
        Has("HandleSaveAsAfterExternalChangeAsync", mine, "var plaintext = DocumentText.ApplyLineEnding(inMemory, _lineEnding);");
        Has("HandleSaveAsAfterExternalChangeAsync", mine, "Secure.SecureMarkdownFile.Save(picked, plaintext, savePw)");
        OnlyEncodeIs("HandleSaveAsAfterExternalChangeAsync", mine,
            "File.WriteAllBytesAsync(picked, DocumentText.Encode(inMemory, _lineEnding, _hadBom))");

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Source as the wiring pin compares it: comments dropped (a call named in a
    /// comment is not a call), then every whitespace character removed, so a reformat - an
    /// argument per line, another indent - doesn't break a pin. A comment is anything from
    /// "//" to the end of the line, so a "//" inside a string literal drops the rest of
    /// that line as well.</summary>
    private static string Code(string source) =>
        Regex.Replace(Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline), @"\s+", "");

    [Fact]
    public void NewDocumentsDefaultToLf()
    {
        // Markdown's ecosystem writes LF (GitHub, every static-site generator, git's
        // own normalisation), and the formatted view's serialiser emits LF already,
        // so a new document saved LF is the file that surprises nobody.
        Assert.Equal(LineEnding.Lf, DocumentText.DefaultLineEnding);
        var fresh = DocumentText.NewDocument;
        Assert.Equal(string.Empty, fresh.Text);
        Assert.Equal(LineEnding.Lf, fresh.Ending);
        Assert.False(fresh.HadBom);
    }
}
