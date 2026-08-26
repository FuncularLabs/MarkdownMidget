using System;
using System.IO;
using System.Reflection;

namespace MarkdownMidget;

/// <summary>
/// The answer to a field report that reads "it just crashes": before this existed,
/// an unhandled exception anywhere — including one QUEUED on the dispatcher and
/// detonated by the nested message pump a native file dialog runs — killed the
/// process with nothing written anywhere. The report is then a symptom with no
/// evidence, and the only diagnostic path is asking a user to spelunk Event Viewer.
///
/// Everything here is best-effort by design: a crash logger that can itself throw
/// turns one failure into two.
/// </summary>
internal static class CrashLog
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "crash.log");

    // Keep the file bounded: a crash loop must not eat the disk. When it grows past
    // this, the oldest half is dropped — recent crashes are the ones being asked about.
    private const long MaxBytes = 512 * 1024;

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            var entry =
                $"---------------------------------------------{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  v{version}  {kind}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}";

            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
            {
                var text = File.ReadAllText(LogPath);
                File.WriteAllText(LogPath, text[(text.Length / 2)..]);
            }
            File.AppendAllText(LogPath, entry);
        }
        catch { /* never let logging be the second crash */ }
    }
}
