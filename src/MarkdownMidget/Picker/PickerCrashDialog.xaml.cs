using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace MarkdownMidget.Picker;

/// <summary>
/// The notice shown when Windows' file dialog took its helper process down: what happened,
/// that the built-in picker takes over, and the clues (<see cref="PickerCrashClues"/>),
/// gathered off the UI thread while the notice is already up.
/// </summary>
public partial class PickerCrashDialog : Window
{
    private readonly string _folder;
    private string? _details, _log;

    private PickerCrashDialog(string folder)
    {
        InitializeComponent();
        _folder = folder;
        ExplorerBtn.ToolTip = $"Opens {folder} in Explorer, which loads the same add-ons in its own process: " +
                              "look at the badges on the files there and at their right-click menus.";
    }

    /// <summary>Show the notice, modal to <paramref name="owner"/>, for a helper that died.</summary>
    internal static void ShowFor(Window owner, FilePickerRequest request, int exitCode, int processId)
    {
        var crashed = DateTimeOffset.Now;   // the helper has only just died; the clues take seconds more
        var folder = PickerCrashClues.FolderToShow(request, Directory.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var dialog = new PickerCrashDialog(folder) { Owner = owner };
        dialog.Loaded += async (_, _) =>
        {
            PickerCrashFindings findings;
            try { findings = await Task.Run(() => PickerCrashSources.Gather(exitCode, processId)); }
            catch (Exception ex)
            {
                CrashLog.Write("PickerCrashSources", ex);
                findings = new PickerCrashFindings(exitCode, null, FaultKind.None, []);
            }
            dialog.Fill(findings, crashed);
            // Once the clues are up, and off the UI thread: a slow or failing disk never holds them back.
            var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownMidget", "logs");
            var details = dialog._details!;
            var (path, note) = await Task.Run(() => PickerCrashClues.SaveLog(logs, details, crashed));
            dialog.ShowLog(path, note, findings);
        };
        dialog.ShowDialog();
    }

    private void Fill(PickerCrashFindings findings, DateTimeOffset crashed)
    {
        FaultText.Text = PickerCrashClues.FaultSentence(findings);
        SuspectsText.Text = PickerCrashClues.SuspectLines(findings);
        OthersText.Text = PickerCrashClues.OthersLine(findings);
        var version = typeof(PickerCrashDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        _details = PickerCrashClues.LogText(PickerCrashClues.Details(findings, version, Environment.OSVersion.VersionString), crashed);
    }

    /// <summary>Where the log was saved, or why it wasn't; without one, Copy details takes Open the log's place.</summary>
    private void ShowLog(string? path, string note, PickerCrashFindings findings)
    {
        _log = path;
        LogText.Text = note;
        LogText.Visibility = Visibility.Visible;
        LogBtn.IsEnabled = path is not null;
        if (path is null) { LogBtn.Visibility = Visibility.Collapsed; CopyBtn.Visibility = Visibility.Visible; OthersText.Text = PickerCrashClues.OthersLine(findings, false); }
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_details is null) return;
        Status(PickerCrashClues.CopyTo(_details, Clipboard.SetText) ?? "Copied. Paste it into your report.");
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        // explorer.exe itself, not ShellExecute: asking the shell to open a folder from here
        // could load the same add-ons into THIS process, which is what the helper was for.
        try
        {
            Process.Start(PickerCrashClues.ExplorerStart(Environment.GetFolderPath(Environment.SpecialFolder.Windows), _folder))?.Dispose();
        }
        catch (Exception ex) { Status("Couldn't open Explorer: " + ex.Message); }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_log is null) return;
        try { Process.Start(PickerCrashClues.ExplorerStart(Environment.GetFolderPath(Environment.SpecialFolder.Windows), _log))?.Dispose(); }
        catch (Exception ex) { Status("Couldn't open the log: " + ex.Message); }
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownMidget");
            var path = PickerCrashClues.ExtractGuide(
                () => typeof(PickerCrashDialog).Assembly.GetManifestResourceStream(PickerCrashClues.GuideResource), dataDir);
            if (path is null) { Status("The guide is missing from this copy of Markdown Midget."); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Status("Couldn't open the guide: " + ex.Message); }
    }
}
