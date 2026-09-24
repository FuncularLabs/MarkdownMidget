using System;
using System.IO;
using System.Linq;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The themes in the repository's community-themes folder: other people's work, shipped
/// as they wrote it and NOT embedded in the exe. They are not held to what a built-in is
/// held to - no contrast sweep, no variables-only rule; they rely on rules of their own -
/// but a file handed to readers has to be one the app will accept, or its menu entry
/// arrives greyed out, and it must not take a built-in's place in the menu.
/// </summary>
public class CommunityThemeTests
{
    private static string Folder => Path.Combine(RepoSources.Root(), "community-themes");

    private static readonly string[] Files = { "Amber-Phosphor-CRT.css", "Code-Breaker.css", "Patriot.css" };

    public static TheoryData<string> CommunityThemes()
    {
        var data = new TheoryData<string>();
        foreach (var file in Files) data.Add(file);
        return data;
    }

    [Fact]
    public void TheFolderHoldsExactlyTheThemesItsReadmeDescribes()
        // Named, because a theory over whatever the folder holds passes on an empty folder.
        => Assert.Equal(Files,
            Directory.EnumerateFiles(Folder, "*.css").Select(Path.GetFileName)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray());

    [Theory]
    [MemberData(nameof(CommunityThemes))]
    public void EveryCommunityThemeIsOneTheAppWouldAccept(string file)
        => Assert.Null(CssValidator.Validate(File.ReadAllText(Path.Combine(Folder, file))));

    [Theory]
    [MemberData(nameof(CommunityThemes))]
    public void NoCommunityThemeTakesABuiltInsPlaceInTheMenu(string file)
    {
        // ThemeStore keys a theme by its file name, and a custom file with a built-in's name
        // REPLACES the built-in: the built-in's entry goes, and a choice saved for it opens the
        // custom file instead. So Joe's Amber-Phosphor.css, dropped into custom\ as it came,
        // would quietly swap the palette-only built-in for the scanlines-and-glow version -
        // hence Amber-Phosphor-CRT.css. The menu NAME must differ too: a different file with
        // the same display name lists twice under one name, with only a separator between.
        var builtIns = typeof(ThemeStore).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("themes/", StringComparison.Ordinal))
            .Select(n => n["themes/".Length..]).ToArray();
        Assert.True(builtIns.Length >= 10, "no built-ins found: the premise failed");
        Assert.DoesNotContain(file, builtIns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ThemeStore.DisplayName(file), builtIns.Select(ThemeStore.DisplayName),
            StringComparer.OrdinalIgnoreCase);
    }
}
