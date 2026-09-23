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
    private PickerCrashFindings? _findings;

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
            dialog.Fill(findings);
        };
        dialog.ShowDialog();
    }

    private void Fill(PickerCrashFindings findings)
    {
        _findings = findings;
        FaultText.Text = PickerCrashClues.FaultSentence(findings);
        SuspectsText.Text = PickerCrashClues.SuspectLines(findings);
        OthersText.Text = PickerCrashClues.OthersLine(findings);
        CopyBtn.IsEnabled = true;
    }

    private void Status(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_findings is null) return;
        var version = typeof(PickerCrashDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var text = PickerCrashClues.Details(_findings, version, Environment.OSVersion.VersionString);
        Status(PickerCrashClues.CopyTo(text, Clipboard.SetText) ?? "Copied. Paste it into your report.");
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        // explorer.exe itself, not ShellExecute: asking the shell to open a folder from here
        // could load the same add-ons into THIS process, which is what the helper was for.
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                                               PickerCrashClues.ExplorerArguments(_folder)) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) { Status("Couldn't open Explorer: " + ex.Message); }
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
