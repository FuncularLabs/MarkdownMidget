using System.Globalization;
using System.Reflection;

namespace MarkdownMidget.Picker;

/// <summary>Why "Always use the built-in file picker" last changed, as settings.json keeps it
/// beside the setting: the reason (<see cref="PickerOffer.Crash"/> or <see cref="PickerOffer.User"/>),
/// the version that recorded it, and when, in UTC.</summary>
internal sealed class PickerSwitch
{
    public string? Reason { get; set; }
    public string? Version { get; set; }
    public DateTime Utc { get; set; }
}

/// <summary>
/// The reason the built-in picker is on, and the one-time offer of Windows' dialog (#12,
/// item 6). Versions before 1.0.0-rc3 switched the picker on whenever Cancel was pressed in
/// Windows' dialog, and recorded nothing. This version records every switch, so the setting on
/// with no reason is the one state that may be that old mistake: then, and only until the
/// question is answered, a file dialog asks first. settings.json is the record (logs are pruned
/// and can be deleted); every write goes through MainWindow.SavePersistentField, and
/// CarryFromDisk keeps another window from erasing it.
/// </summary>
internal static class PickerOffer
{
    public const string Crash = "crash", User = "user";

    public const string Question = "Markdown Midget is using its own file picker. An older version could switch to it " +
        "by mistake when you pressed Cancel. Try Windows' dialog again?";

    public static string AppVersion =>
        typeof(PickerOffer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public static PickerSwitch Record(string reason, string version, DateTime utc) => new() { Reason = reason, Version = version, Utc = utc };

    public static bool ShouldAsk(bool builtInOn, PickerSwitch? recorded, bool answered) =>
        builtInOn && recorded?.Reason is null && !answered;

    /// <summary>The setting changed, by the crash switch or the user: set it and say why.</summary>
    public static void Switch(MainWindow.AppSettings s, bool on, string reason, string version, DateTime utc)
    {
        s.UseBuiltInPicker = on;
        s.BuiltInPickerSwitch = Record(reason, version, utc);
    }

    /// <summary>The offer answered, either way, so it is never asked again; Windows' dialog also
    /// turns the setting off, as the user's own choice.</summary>
    public static void Answer(MainWindow.AppSettings s, bool useWindows, string version, DateTime utc)
    {
        s.BuiltInPickerOfferAnswered = true;
        if (useWindows) Switch(s, false, User, version, utc);
    }

    /// <summary>The crash log's line for the switch the crash made.</summary>
    public static string LogLine(PickerSwitch s) =>
        $"The built-in file picker was switched on: reason {s.Reason}, by Markdown Midget {s.Version}, " +
        $"at {s.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC";
}
