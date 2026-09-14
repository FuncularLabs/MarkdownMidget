using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MarkdownMidget;

/// <summary>Opt-in phase timings (MDM_TIMING=1): a line per phase in %TEMP%\MarkdownMidget-timing.log. Unset, nothing is timed, written or asked.</summary>
internal static class TimingLog
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MDM_TIMING") == "1";
    public static string Line(DateTime at, string sequence, string phase, double ms) =>   // spelled the same in every culture
        string.Create(CultureInfo.InvariantCulture, $"{at:HH:mm:ss.fff} {sequence} {phase} {ms:F0}ms");
    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;
    /// <summary>Logs the phase begun at <paramref name="started"/> and returns the next phase's start.</summary>
    public static long Lap(string sequence, string phase, long started) { Write(sequence, phase, Enabled ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0); return Start(); }
    public static void Write(string sequence, string phase, double ms)
    {
        if (Enabled) try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "MarkdownMidget-timing.log"), Line(DateTime.Now, sequence, phase, ms) + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a log must never break the app
    }
}
