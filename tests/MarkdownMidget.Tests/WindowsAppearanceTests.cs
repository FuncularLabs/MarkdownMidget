using System;
using System.Collections.Generic;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Windows' app mode, read through the seam: nothing here touches the registry or
/// subscribes to system events.
/// </summary>
public class WindowsAppearanceTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]          // the value is missing: Windows' default, light
    [InlineData("garbage", false)]
    [InlineData("0", false)]           // a string is not the DWORD Windows writes
    public void AppsUseLightThemeDecidesDarkMode(object? appsUseLightTheme, bool dark)
    {
        using var appearance = new WindowsAppearance(() => appsUseLightTheme, () => false);
        Assert.Equal(dark, appearance.IsDark);
    }

    [Fact]
    public void HighContrastCountsAsLight()
    {
        using var appearance = new WindowsAppearance(() => 0, () => true);
        Assert.False(appearance.IsDark);
    }

    [Fact]
    public void ChangedIsRaisedOncePerFlipOfTheEffectiveMode()
    {
        object? apps = 1;
        var highContrast = false;
        using var appearance = new WindowsAppearance(() => apps, () => highContrast);
        var raised = 0;
        appearance.Changed += (_, _) => raised++;

        // Other preference changes: nothing to report.
        appearance.Refresh();
        appearance.Refresh();
        Assert.Equal(0, raised);

        apps = 0;
        appearance.Refresh();
        Assert.True(appearance.IsDark);
        Assert.Equal(1, raised);

        appearance.Refresh();          // Windows broadcasts one switch several times
        Assert.Equal(1, raised);

        highContrast = true;
        appearance.Refresh();
        Assert.False(appearance.IsDark);
        Assert.Equal(2, raised);

        apps = 1;                      // light under high contrast: still light
        appearance.Refresh();
        highContrast = false;
        appearance.Refresh();
        Assert.False(appearance.IsDark);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ABurstOfNotificationsIsReadOnceAfterItSettles()
    {
        object? apps = 1;
        var scheduled = new List<Action>();
        using var appearance = new WindowsAppearance(() => apps, () => false, scheduled.Add);
        var raised = 0;
        appearance.Changed += (_, _) => raised++;

        apps = 0;
        appearance.OnPreferenceChanged();
        appearance.OnPreferenceChanged();
        appearance.OnPreferenceChanged();
        Assert.Single(scheduled);
        Assert.Equal(0, raised);          // nothing until the burst is over

        scheduled[0]();
        Assert.True(appearance.IsDark);
        Assert.Equal(1, raised);

        appearance.OnPreferenceChanged(); // a later change is read again
        Assert.Equal(2, scheduled.Count);
        scheduled[1]();
        Assert.Equal(1, raised);          // and, unchanged, raises nothing
    }

    // ===== View ▸ Mode =====

    [Fact]
    public void AModeReadFromSettingsRaisesChangedOnlyWhenTheEffectiveModeFlips()
    {
        object? apps = 1;                  // Windows light
        AppearanceMode? saved = null;      // unreadable at launch: System
        var scheduled = new List<Action>();
        using var appearance = new WindowsAppearance(() => apps, () => false, scheduled.Add, () => saved);
        var raised = 0;
        appearance.Changed += (_, _) => raised++;
        void Notified() { appearance.OnPreferenceChanged(); scheduled[^1](); }   // settings.json or Windows changed

        saved = AppearanceMode.Light;      // another window picked Light: light already
        Notified();
        Assert.Equal((AppearanceMode.Light, false, 0), (appearance.Mode, appearance.IsDark, raised));

        saved = AppearanceMode.Dark;
        Notified();
        Assert.Equal((AppearanceMode.Dark, true, 1), (appearance.Mode, appearance.IsDark, raised));

        saved = null;                      // a read that failed keeps the last answer
        apps = 0;                          // and Windows going dark under Dark changes nothing
        Notified();
        Assert.Equal((AppearanceMode.Dark, true, 1), (appearance.Mode, appearance.IsDark, raised));

        saved = AppearanceMode.System;     // back to System with Windows dark: still dark
        Notified();
        Assert.Equal((AppearanceMode.System, true, 1), (appearance.Mode, appearance.IsDark, raised));

        saved = AppearanceMode.Light;
        Notified();
        Assert.Equal((AppearanceMode.Light, false, 2), (appearance.Mode, appearance.IsDark, raised));
    }

    [Fact]
    public void ThePickInThisWindowAppliesAtOnceAndRaisesOnlyOnAFlip()
    {
        using var appearance = new WindowsAppearance(() => 1, () => false, savedMode: () => AppearanceMode.System);
        var raised = 0;
        appearance.Changed += (_, _) => raised++;

        appearance.SetMode(AppearanceMode.Light);
        Assert.Equal((AppearanceMode.Light, false, 0), (appearance.Mode, appearance.IsDark, raised));
        appearance.SetMode(AppearanceMode.Dark);
        Assert.Equal((AppearanceMode.Dark, true, 1), (appearance.Mode, appearance.IsDark, raised));
    }

    [Fact]
    public void HighContrastWinsOverDark()
    {
        var highContrast = true;
        using var appearance = new WindowsAppearance(() => 1, () => highContrast, savedMode: () => AppearanceMode.Dark);
        Assert.Equal((AppearanceMode.Dark, false), (appearance.Mode, appearance.IsDark));
        highContrast = false;
        appearance.Refresh();
        Assert.True(appearance.IsDark);
    }

    [Fact]
    public void TheChromeGetsTheEffectiveModeAtStartAndOnEveryChange()
    {
        // MainWindow passes ChromePalette.Apply, before the window is first shown.
        var highContrast = false;
        AppearanceMode? saved = AppearanceMode.Dark;
        using var appearance = new WindowsAppearance(() => 1, () => highContrast, savedMode: () => saved);
        var applied = new List<bool>();

        appearance.Follow(applied.Add);
        Assert.Equal(new[] { true }, applied);        // Dark over Windows light

        highContrast = true;
        appearance.Refresh();
        highContrast = false;
        saved = AppearanceMode.System;
        appearance.Refresh();                         // System with Windows light: no flip, no call
        appearance.SetMode(AppearanceMode.Dark);
        Assert.Equal(new[] { true, false, true }, applied);
    }

    [Fact]
    public void AClosedWindowsAppearanceRaisesNothing()
    {
        // A re-read scheduled just before the window closed runs after it.
        object? apps = 1;
        var scheduled = new List<Action>();
        var appearance = new WindowsAppearance(() => apps, () => false, scheduled.Add);
        var raised = 0;
        appearance.Changed += (_, _) => raised++;
        appearance.OnPreferenceChanged();

        appearance.Dispose();
        apps = 0;
        scheduled[0]();
        appearance.Refresh();
        appearance.OnPreferenceChanged();

        Assert.Equal(0, raised);
        Assert.Single(scheduled);
    }
}
