using System;
using System.IO;
using System.Linq;
using MarkdownMidget.Picker;
using MarkdownMidget.Secure;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Windows' dialog and the built-in picker are two views of ONE FilePickerRequest:
/// FilePickerService.Apply configures Windows' dialog from it, and MidgetFilePicker lists
/// FilePickerModel.ParseFilter of the same Filter, selects FilterIndex, and shows a file
/// when FilePickerModel.MatchesFilter passes it. These pin, for each call site's request,
/// that both offer the same types in the same order with the same default, and that
/// .mdenc is listed exactly where Windows lists it: under a type that names it and under
/// All files. The picker window itself is not built here: its XAML icon only resolves
/// inside the app.
/// </summary>
public class PickerParityTests
{
    private const string EncryptFilter = "Secure Markdown (*.mdenc)|*.mdenc";

    /// <summary>MainWindow's requests, by call site: File ▸ Open, Save As, File ▸ Encrypt
    /// Document on an untitled document, and "Save your current version as…" after an
    /// external change. Only File ▸ Open reads the setting.</summary>
    private static FilePickerRequest Site(string site) => site switch
    {
        "Open, setting off" => new() { Filter = SecureUi.OpenFilter(false), DefaultExt = ".md", CheckFileExists = true },
        "Open, setting on" => new() { Filter = SecureUi.OpenFilter(true), DefaultExt = ".md", CheckFileExists = true },
        "Save As, plain" => new() { Save = true, Filter = SecureUi.SaveFilter, FilterIndex = 1, DefaultExt = ".md" },
        "Save As, encrypted" => new() { Save = true, Filter = SecureUi.SaveFilter, FilterIndex = SecureUi.SaveFilterEncryptedIndex, DefaultExt = SecureMarkdownFormat.Extension },
        "Encrypt Document, untitled" => new() { Save = true, Filter = EncryptFilter, DefaultExt = SecureMarkdownFormat.Extension },
        "Save your version, encrypted" => new() { Save = true, Filter = EncryptFilter + "|All files (*.*)|*.*", DefaultExt = SecureMarkdownFormat.Extension },
        "Save your version, plain" => new() { Save = true, Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*", DefaultExt = ".md" },
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
    };

    public static TheoryData<string> Sites => new("Open, setting off", "Open, setting on", "Save As, plain",
        "Save As, encrypted", "Encrypt Document, untitled", "Save your version, encrypted", "Save your version, plain");

    private static FilterGroup Type(FilePickerRequest request, string label) =>
        FilePickerModel.ParseFilter(request.Filter).Single(g => g.Label == label);

    [Theory, MemberData(nameof(Sites))]
    public void TheBuiltInPickerOffersWindowsTypesInOrderWithTheSameDefault(string site)
    {
        var request = Site(site);
        Microsoft.Win32.FileDialog windows = request.Save
            ? new Microsoft.Win32.SaveFileDialog() : new Microsoft.Win32.OpenFileDialog();
        FilePickerService.Apply(windows, request);
        var parts = windows.Filter.Split('|');
        var windowsTypes = Enumerable.Range(0, parts.Length / 2).Select(i => parts[2 * i] + "|" + parts[2 * i + 1]).ToList();

        var builtIn = FilePickerModel.ParseFilter(request.Filter);
        Assert.Equal(windowsTypes, builtIn.Select(g => g.Label + "|" + string.Join(";", g.Patterns)));
        // In range, so the picker's clamp selects exactly the type Windows selects.
        Assert.InRange(windows.FilterIndex, 1, builtIn.Count);
        Assert.Equal(request.FilterIndex, windows.FilterIndex);
        Assert.Equal(request.DefaultExt!.TrimStart('.'), windows.DefaultExt);
        Assert.Contains(builtIn, g => g.IsCatchAll || g.Patterns.Contains("*.mdenc"));
    }

    [Theory]
    [InlineData("Open, setting off", "Markdown (*.md;*.markdown)", "a.md b.markdown")]
    [InlineData("Open, setting off", "All files (*.*)", "a.md b.markdown c.mdenc d.txt e.pdf")]
    [InlineData("Open, setting on", "Markdown (*.md;*.markdown;*.mdenc)", "a.md b.markdown c.mdenc")]
    [InlineData("Open, setting on", "All files (*.*)", "a.md b.markdown c.mdenc d.txt e.pdf")]
    [InlineData("Save As, plain", "Markdown (*.md)", "a.md")]
    [InlineData("Save As, encrypted", "Secure Markdown (*.mdenc)", "c.mdenc")]
    [InlineData("Save As, encrypted", "All files (*.*)", "a.md b.markdown c.mdenc d.txt e.pdf")]
    [InlineData("Encrypt Document, untitled", "Secure Markdown (*.mdenc)", "c.mdenc")]
    [InlineData("Save your version, encrypted", "All files (*.*)", "a.md b.markdown c.mdenc d.txt e.pdf")]
    public void TheListShowsMdencUnderTheTypesThatNameItAndUnderAllFiles(string site, string type, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdm-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in "a.md b.markdown c.mdenc d.txt e.pdf".Split(' '))
                File.WriteAllText(Path.Combine(dir, name), "x");
            var group = Type(Site(site), type);
            var listed = Directory.EnumerateFiles(dir).Select(Path.GetFileName)
                .Where(name => FilePickerModel.MatchesFilter(name!, group))
                .Order(StringComparer.Ordinal);
            Assert.Equal(expected.Split(' '), listed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("Open, setting off", @"C:\d\secret.mdenc", @"C:\d\secret.mdenc")]
    [InlineData("Save As, plain", @"C:\d\notes.mdenc", @"C:\d\notes.mdenc")]
    [InlineData("Save As, encrypted", @"C:\d\notes", @"C:\d\notes.mdenc")]
    [InlineData("Encrypt Document, untitled", @"C:\d\notes", @"C:\d\notes.mdenc")]
    public void ATypedMdencNameIsKeptAndABareNameUnderSecureMarkdownGetsIt(string site, string typed, string expected)
    {
        // The type the picker opens on, and the extension rule Accept applies with it.
        var request = Site(site);
        var opensOn = FilePickerModel.ParseFilter(request.Filter)[request.FilterIndex - 1];
        Assert.Equal(expected, FilePickerModel.EnsureExtension(typed, opensOn, request.DefaultExt));
    }
}
