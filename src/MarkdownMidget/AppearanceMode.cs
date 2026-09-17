using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MarkdownMidget;

/// <summary>View ▸ Mode: follow Windows' app mode, or always light, or always dark.</summary>
internal enum AppearanceMode { System, Light, Dark }

/// <summary>
/// The effective mode, one answer for the window chrome and the document theme slots, and
/// the settings.json field View ▸ Mode is saved in (<c>AppearanceMode</c>: "System", "Light"
/// or "Dark"; missing, null or anything else is System).
/// </summary>
internal static class AppearanceModes
{
    /// <summary>The settings.json property, spelled as MainWindow.AppSettings serializes it.</summary>
    private const string SettingName = "AppearanceMode";

    /// <summary>Dark or light. High contrast always wins, as light: a contrast theme brings its
    /// own colours. Otherwise Light and Dark ignore Windows, and System follows it.</summary>
    public static bool IsDark(AppearanceMode mode, bool windowsDark, bool highContrast) =>
        !highContrast && mode switch
        {
            AppearanceMode.Light => false,
            AppearanceMode.Dark => true,
            _ => windowsDark,
        };

    /// <summary>A saved value. Not Enum.TryParse, which would take "1" or "Light, Dark".</summary>
    public static AppearanceMode Parse(string? saved) =>
        string.Equals(saved, nameof(AppearanceMode.Light), StringComparison.OrdinalIgnoreCase) ? AppearanceMode.Light
        : string.Equals(saved, nameof(AppearanceMode.Dark), StringComparison.OrdinalIgnoreCase) ? AppearanceMode.Dark
        : AppearanceMode.System;

    /// <summary>
    /// The mode saved in the settings file at <paramref name="settingsPath"/>, for a window
    /// following another window's pick. No file is System, as at a launch. Null when the file
    /// can't be read or parsed: no answer, so the caller keeps what it has. Reads only: unlike
    /// a launch, a corrupt file is not moved aside. The file is open only while it is read; a
    /// save that lands in that moment fails to replace it and tries again (MainWindow.ReplaceFile).
    /// </summary>
    public static AppearanceMode? ReadSetting(string settingsPath)
    {
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var json = JsonDocument.Parse(reader.ReadToEnd());   // byte-order marks as File.ReadAllText
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty(SettingName, out var value) && value.ValueKind == JsonValueKind.String
                ? Parse(value.GetString())
                : AppearanceMode.System;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return AppearanceMode.System;
        }
        catch { return null; }
    }

    /// <summary>The greyed top line of View ▸ Theme: which mode a pick sets. It names Windows
    /// only when Windows decides.</summary>
    public static string ThemeCaption(AppearanceMode mode, bool dark) =>
        (mode == AppearanceMode.System ? "For Windows " : "For ") + (dark ? "dark mode" : "light mode");
}
