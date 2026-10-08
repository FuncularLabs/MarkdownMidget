using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The built-in picker's sortable columns (#12, item 3). The picker window can't be built
/// here (its XAML icon only resolves inside the app), so these pin the pure halves it
/// calls; TEST-PLAN-1.0.md DLG-05 checks the window by hand.
/// </summary>
public class PickerSortTests
{
    private static readonly DateTime Day = new(2026, 10, 8, 9, 0, 0);

    private static PickerSortKey Dir(string name, DateTime at) => new(true, name, at, -1, FileTypeNames.Folder);

    private static PickerSortKey File(string name, long bytes, DateTime? at, string type = "X File") => new(false, name, at, bytes, type);

    /// <summary>Each column orders the files differently from their names and from each
    /// other; B.txt's capital puts it first in any order that minds case. A file comes
    /// first, so folders-first is the sort's doing.</summary>
    private static List<PickerSortKey> Listing() =>
    [
        File("c.md", 2048, Day.AddDays(-3), "Markdown File"), Dir("Zeta", Day.AddDays(-5)),
        File("B.txt", 900, Day.AddDays(-1), "Text Document"), File("a.pdf", 10240, Day.AddDays(-2), "Adobe PDF Document"),
        Dir("alpha", Day.AddDays(-1)),
    ];

    /// <summary>Theories name the column as text: PickerColumn is internal, and a public test can't take it.</summary>
    private static PickerSort By(string column, bool descending) => new(Enum.Parse<PickerColumn>(column), descending);

    private static List<PickerSortKey> Sort(PickerSort sort, List<PickerSortKey>? keys = null)
    {
        var list = keys ?? Listing();
        list.Sort((a, b) => FilePickerModel.CompareEntries(a, b, sort));
        return list;
    }

    private static string Names(PickerSort sort, List<PickerSortKey>? keys = null) => string.Join(" ", Sort(sort, keys).Select(k => k.Name));

    [Fact]
    public void WithNothingRememberedAPickerOpensFoldersFirstThenNamesAToZ()
    {
        Assert.Equal(new PickerSort(PickerColumn.Name, Descending: false), PickerSort.Default);
        Assert.Equal("alpha Zeta a.pdf B.txt c.md", Names(PickerSort.Default));
    }

    [Theory]
    [InlineData("Name", false, "alpha Zeta a.pdf B.txt c.md")]
    [InlineData("Name", true, "Zeta alpha c.md B.txt a.pdf")]
    [InlineData("Modified", false, "Zeta alpha c.md a.pdf B.txt")]
    [InlineData("Modified", true, "alpha Zeta B.txt a.pdf c.md")]
    [InlineData("Type", false, "alpha Zeta a.pdf c.md B.txt")]
    [InlineData("Type", true, "alpha Zeta B.txt c.md a.pdf")]
    [InlineData("Size", false, "alpha Zeta B.txt c.md a.pdf")]
    [InlineData("Size", true, "alpha Zeta a.pdf c.md B.txt")]
    public void EachColumnSortsBothWays(string column, bool descending, string expected) =>
        Assert.Equal(expected, Names(By(column, descending)));

    [Fact]
    public void FoldersStayBeforeFilesInEverySort()
    {
        foreach (var column in Enum.GetValues<PickerColumn>())
            foreach (var descending in new[] { false, true })
                Assert.Equal([true, true, false, false, false], Sort(new PickerSort(column, descending)).Select(k => k.IsDirectory));
    }

    [Fact]
    public void TiesBreakByNameAToZWhicheverWayTheColumnRuns()
    {
        // Same date, size and type. "A" and "a" differ only in case (a case-sensitive folder
        // can hold both), and still get one place each: capital first, by exact spelling.
        foreach (var column in new[] { PickerColumn.Modified, PickerColumn.Type, PickerColumn.Size })
            foreach (var descending in new[] { false, true })
                Assert.Equal("A a b c", Names(new PickerSort(column, descending), [File("b", 5, Day), File("a", 5, Day), File("c", 5, Day), File("A", 5, Day)]));
    }

    [Fact]
    public void SizesSortAsNumbersNotAsTheirText()
    {
        // As text the files would go "" "1.5 MB" "10.0 KB" "2.0 KB" "900 B": e b c a d. A folder's
        // blank size and a size that couldn't be read are both -1.
        List<PickerSortKey> keys =
            [File("a", 2048, Day), File("b", 1572864, Day), File("c", 10240, Day), File("d", 900, Day), File("e", -1, Day), Dir("folder", Day)];
        Assert.Equal("folder e d a c b", Names(By("Size", false), keys));
        Assert.Equal("folder b c a d e", Names(By("Size", true), keys));
    }

    [Fact]
    public void DatesSortByTheirTimestampNotTheirText()
    {
        // As "g" text in en-US: "1/2/2026 8:00 AM" < "10/8/2026 10:00 AM" < "10/8/2026 9:00 AM"
        // < "12/31/2025 11:00 PM". A date that couldn't be read is the oldest.
        List<PickerSortKey> keys =
        [
            File("a", 1, new DateTime(2026, 10, 8, 10, 0, 0)), File("b", 1, new DateTime(2025, 12, 31, 23, 0, 0)),
            File("c", 1, new DateTime(2026, 10, 8, 9, 0, 0)), File("d", 1, new DateTime(2026, 1, 2, 8, 0, 0)), File("e", 1, null),
        ];
        Assert.Equal("e b d c a", Names(By("Modified", false), keys));
        Assert.Equal("a c d b e", Names(By("Modified", true), keys));
    }

    [Fact]
    public void ClickingAHeaderSortsByItAndClickingItAgainTurnsItRound()
    {
        var sort = PickerSort.Default;
        Assert.Equal(new PickerSort(PickerColumn.Name, true), sort = sort.Click(PickerColumn.Name));
        Assert.Equal(new PickerSort(PickerColumn.Name, false), sort = sort.Click(PickerColumn.Name));
        Assert.Equal(new PickerSort(PickerColumn.Size, false), sort = sort.Click(PickerColumn.Size));
        Assert.Equal(new PickerSort(PickerColumn.Size, true), sort = sort.Click(PickerColumn.Size));
        Assert.Equal(new PickerSort(PickerColumn.Type, false), sort = sort.Click(PickerColumn.Type));
        Assert.Equal(new PickerSort(PickerColumn.Type, true), sort = sort.Click(PickerColumn.Type));
        // Date modified starts newest first: finding the file just written is why it gets clicked.
        Assert.Equal(new PickerSort(PickerColumn.Modified, true), sort = sort.Click(PickerColumn.Modified));
        Assert.Equal(new PickerSort(PickerColumn.Modified, false), sort.Click(PickerColumn.Modified));
    }

    [Theory]
    [InlineData("Name", false, "Name  ▲|Date modified|Type|Size")]
    [InlineData("Name", true, "Name  ▼|Date modified|Type|Size")]
    [InlineData("Modified", true, "Name|Date modified  ▼|Type|Size")]
    [InlineData("Type", false, "Name|Date modified|Type  ▲|Size")]
    [InlineData("Size", true, "Name|Date modified|Type|Size  ▼")]
    public void OnlyTheSortedColumnsHeaderShowsAnArrowPointingItsWay(string column, bool descending, string expected) =>
        Assert.Equal(expected, string.Join("|", Enum.GetValues<PickerColumn>().Select(c => FilePickerModel.HeaderText(c, By(column, descending)))));

    [Fact]
    public void NamesAndTypesCompareByCharacterCodeIgnoringCase()
    {
        // Case folded, then by code: "_" after the letters and "Ä" after "B" (a culture's order puts
        // "_x" first), and "alpha" before "Beta" (a case-sensitive one puts the capital first).
        List<PickerSortKey> keys = [File("Birne", 1, Day, "Beta Type"), File("_x", 1, Day, "Beta Type"), File("Äpfel", 1, Day, "Beta Type"), File("apple", 1, Day, "alpha Type")];
        Assert.Equal("apple Birne _x Äpfel", Names(By("Name", false), keys));
        Assert.Equal("apple Birne _x Äpfel", Names(By("Type", false), keys));
    }

    [Fact]
    public void FindByPrefixCountsRowsInTheOrderItIsGiven()
    {
        var names = Sort(By("Size", true)).Select(k => k.Name).ToList();   // alpha Zeta a.pdf c.md B.txt
        var first = FilePickerModel.FindByPrefix(names, "a", -1);
        Assert.Equal("alpha", names[first]);
        Assert.Equal("a.pdf", names[FilePickerModel.FindByPrefix(names, "a", first)]);
        Assert.Equal(3, FilePickerModel.FindByPrefix(names, "c", -1));     // c.md is above B.txt here
    }
}

/// <summary>
/// The Type column (#12, item 2): Windows' registered name for an extension, read as plain
/// registry values and never through a DLL, else "MD File". A fake stands in for
/// HKEY_CLASSES_ROOT, so no test reads the real registry.
/// </summary>
public class FileTypeNamesTests
{
    private sealed class FakeClassesRoot
    {
        public readonly Dictionary<string, object> Values = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Reads = [];
        public Exception? Fails;

        public object? Read(string subKey, string valueName)
        {
            Reads.Add(subKey + "|" + valueName);
            return Fails is null ? Values.GetValueOrDefault(subKey + "|" + valueName) : throw Fails;
        }
    }

    private static FakeClassesRoot Holding(params (string Key, object Value)[] values)
    {
        var registry = new FakeClassesRoot();
        foreach (var (key, value) in values) registry.Values[key] = value;
        return registry;
    }

    [Fact]
    public void AFolderIsFileFolderEvenWithADotInItsName()
    {
        var registry = Holding();
        Assert.Equal("File folder", new FileTypeNames(registry.Read).Of("notes.md", isDirectory: true));
        Assert.Empty(registry.Reads);
    }

    [Fact]
    public void AFileTakesItsProgIdsFriendlyTypeName()
    {
        var registry = Holding((".md|", "MarkdownMidget.md"), ("MarkdownMidget.md|FriendlyTypeName", "Markdown Document"),
            ("MarkdownMidget.md|", "Markdown (old name)"));
        Assert.Equal("Markdown Document", new FileTypeNames(registry.Read).Of("notes.md", isDirectory: false));
    }

    [Theory]
    [InlineData(@"@%SystemRoot%\System32\shell32.dll,-30595")]   // as Windows 11 registers .jpeg
    [InlineData("  ")]
    public void AnIndirectOrBlankFriendlyTypeNameFallsBackToTheProgIdsDefaultValue(string friendlyTypeName)
    {
        var registry = Holding((".jpeg|", "jpegfile"), ("jpegfile|FriendlyTypeName", friendlyTypeName), ("jpegfile|", "JPEG Image"));
        Assert.Equal("JPEG Image", new FileTypeNames(registry.Read).Of("photo.jpeg", isDirectory: false));
    }

    [Fact]
    public void OnWindows11ATextFileIsTxtFile() =>   // .txt names txtfilelegacy there, a ProgID with no values
        Assert.Equal("TXT File", new FileTypeNames(Holding((".txt|", "txtfilelegacy")).Read).Of("notes.txt", isDirectory: false));

    [Theory]
    [InlineData("both names indirect")]
    [InlineData("no ProgID")]
    [InlineData("a ProgID with no names")]
    [InlineData("blank names")]
    [InlineData("not a string")]
    public void WithNoPlainNameTheUpperCasedExtensionNamesIt(string registryHolds)
    {
        var registry = registryHolds switch
        {
            "both names indirect" => Holding((".md|", "mdfile"), ("mdfile|FriendlyTypeName", "@shell32.dll,-1"), ("mdfile|", " @shell32.dll,-2")),
            "a ProgID with no names" => Holding((".md|", "mdfile")),
            "blank names" => Holding((".md|", "mdfile"), ("mdfile|FriendlyTypeName", "  "), ("mdfile|", "")),
            "not a string" => Holding((".md|", new byte[] { 1 })),
            _ => Holding(),
        };
        Assert.Equal("MD File", new FileTypeNames(registry.Read).Of("notes.md", isDirectory: false));
    }

    [Fact]
    public void AKeyThatCannotBeReadFallsBackToTheExtensionAndDoesNotThrow()
    {
        foreach (var failure in new Exception[] { new UnauthorizedAccessException(), new System.Security.SecurityException(), new ArgumentException("too long") })
            Assert.Equal("JPEG File", new FileTypeNames(new FakeClassesRoot { Fails = failure }.Read).Of("photo.jpeg", isDirectory: false));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("name.")]
    public void AFileWithNoExtensionIsJustFile(string name)
    {
        var registry = Holding();
        Assert.Equal("File", new FileTypeNames(registry.Read).Of(name, isDirectory: false));
        Assert.Empty(registry.Reads);
    }

    [Fact]
    public void EachExtensionIsLookedUpOnceForAsLongAsThePickerIsOpen()
    {
        var registry = Holding((".md|", "mdfile"), ("mdfile|", "Markdown File"));
        var types = new FileTypeNames(registry.Read);
        Assert.Equal("Markdown File", types.Of("a.md", isDirectory: false));
        Assert.Equal("Markdown File", types.Of("B.MD", isDirectory: false));   // the registry ignores case too
        Assert.Equal("ZIP File", types.Of("x.zip", isDirectory: false));
        Assert.Equal("ZIP File", types.Of("y.zip", isDirectory: false));       // a fallback is kept as well
        Assert.Equal([".md|", "mdfile|FriendlyTypeName", "mdfile|", ".zip|"], registry.Reads);   // no ".MD|", no second ".zip|"
        // The next picker asks again, so it sees a name registered since.
        new FileTypeNames(registry.Read).Of("c.md", isDirectory: false);
        Assert.Equal(".md|", registry.Reads[4]);
    }
}
