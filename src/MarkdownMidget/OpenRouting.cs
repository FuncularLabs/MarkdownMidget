using System.Diagnostics;
using System.IO;

namespace MarkdownMidget;

/// <summary>Where the files one request opens go: <paramref name="Here"/> (only in a window with
/// no document), <paramref name="Launch"/> (one new instance each), neither because the file is
/// already this window's document (<paramref name="AlreadyHere"/>), or past the per-drop cap.</summary>
internal sealed record OpenRoute(string? Here, IReadOnlyList<string> Launch, bool AlreadyHere, IReadOnlyList<string> OverCap)
{
    public string? Notice() => OverCap.Count == 0 ? null
        : $"At most {OpenRouting.MaxInstancesPerDrop} new windows at once; not opened: {string.Join(", ", OverCap.Select(Path.GetFileName))}";
}

/// <summary>
/// File ▸ Open, Open Recent and a drop on either view never replace the document a window
/// has: they open in place only when it has none (HasNoDocument), and otherwise start this exe
/// once per file. A file already open in ANOTHER window is that instance's question: its
/// startup OpenGuard probe brings the holder forward and exits.
/// </summary>
internal static class OpenRouting
{
    public const int MaxInstancesPerDrop = 10;

    /// <summary>Whether the window takes the first file itself: the "no document" screen, or an untitled
    /// document nobody has changed that is empty. Never while an open into this window is under way: the
    /// window still looks empty then, and a second open would replace what is typed into the first.</summary>
    public static bool HasNoDocument(bool closed, bool untitled, bool modified, string cleanText, int opensUnderWay) =>
        opensUnderWay == 0 && (closed || (untitled && !modified && string.IsNullOrWhiteSpace(cleanText)));

    public static OpenRoute Plan(IEnumerable<string> paths, bool noDocument, string? currentPath)
    {
        string? here = null;
        var alreadyHere = false;
        List<string> launch = [], over = [];
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (currentPath is not null && string.Equals(path, Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase)) alreadyHere = true;
            else if (noDocument && here is null) here = path;
            else if (launch.Count < MaxInstancesPerDrop) launch.Add(path);
            else over.Add(path);
        }
        return new OpenRoute(here, launch, alreadyHere, over);
    }

    /// <summary>The paths of the documents a drop plan opens, from the paths the drop carried
    /// (index-aligned with the plan's files). A document with no path, or a path under another
    /// name, is named in the notice and not opened: opening its bytes as an unsaved copy is what
    /// made a drop look like an open whose Save never reached the file.</summary>
    public static (IReadOnlyList<string> Paths, string? Notice) DocumentPaths(DropPlan plan, IReadOnlyList<string?>? paths)
    {
        if (paths is not null && paths.Count != plan.Files.Count) paths = null;
        List<string> found = [], missing = [];
        foreach (var i in plan.Open)
        {
            if (paths?[i] is { } p && string.Equals(Path.GetFileName(p), plan.Files[i].Name, StringComparison.OrdinalIgnoreCase)) found.Add(p);
            else missing.Add(plan.Files[i].Name);
        }
        return (found, missing.Count == 0 ? null : "Couldn't tell where it is on disk; use File ▸ Open: " + string.Join(", ", missing));
    }

    /// <summary>Start one instance per path through <paramref name="launch"/> (the seam tests fake).
    /// A failure is the returned status note, never a throw; the other paths still start.</summary>
    public static string? LaunchAll(IEnumerable<string> paths, Action<string> launch)
    {
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try { launch(path); }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(path)} ({ex.Message})"); }
        }
        return failed.Count == 0 ? null : "Couldn't open in a new window: " + string.Join(", ", failed);
    }

    /// <summary>The real launch: this exe with the path as its one argument, which App.OnStartup
    /// and MainWindow.DocumentArgument already read as the document to open.</summary>
    public static void StartInstance(string path) =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("the app's own path is unknown"))
        { UseShellExecute = false, ArgumentList = { path } })?.Dispose();
}
