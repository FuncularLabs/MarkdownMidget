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
