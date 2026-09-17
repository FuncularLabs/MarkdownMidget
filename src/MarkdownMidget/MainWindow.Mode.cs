using System;
using System.Windows;
using System.Windows.Controls;

namespace MarkdownMidget;

/// <summary>
/// View ▸ Mode: Light, Dark or System. The effective mode lives in <see cref="_appearance"/>
/// (<see cref="WindowsAppearance"/>); the window chrome and the document theme slots both
/// follow it through its Changed event, wired in InitializeThemes.
/// </summary>
public partial class MainWindow
{
    /// <summary>What a View ▸ Mode pick writes: that one field, merged by
    /// <see cref="SavePersistentField"/> onto the settings on disk.</summary>
    internal static Action<AppSettings> RememberMode(AppearanceMode mode) => s => s.AppearanceMode = mode.ToString();

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || _appearance is null) return;
        var mode = AppearanceModes.Parse(tag);
        // Saved first, then applied here. Every other window's settings watcher reads the
        // pick back and follows it, and writes nothing; so does this window's, finding it
        // already applied. A save that fails leaves the pick on this window only, and says so.
        var saved = SavePersistentField(RememberMode(mode));
        _appearance.SetMode(mode);
        if (!saved) FlashStatus("Couldn't save the mode — it applies to this window until it closes.");
        SyncModeMenu();
        RefocusEditor();
    }

    /// <summary>Ticks the mode in force: on View's opening, since another window's pick can
    /// change it without changing the effective mode, and so without an event here.</summary>
    private void SyncModeMenu()
    {
        var mode = _appearance?.Mode ?? AppearanceMode.System;
        ModeLight.IsChecked = mode == AppearanceMode.Light;
        ModeDark.IsChecked = mode == AppearanceMode.Dark;
        ModeSystem.IsChecked = mode == AppearanceMode.System;
    }
}
