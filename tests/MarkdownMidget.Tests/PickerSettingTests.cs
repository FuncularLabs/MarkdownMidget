using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>"Always use the built-in file picker" across windows (separate processes), and the picker's note.</summary>
public class PickerSettingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AnotherWindowsUnrelatedSaveKeepsThePickerSettingAsItIsOnDisk(bool onDisk, bool inStaleWindow)
    {
        // Window A changed the setting; window B, launched before, toggles word wrap.
        var save = new MainWindow.AppSettings { WordWrap = true, UseBuiltInPicker = inStaleWindow };
        MainWindow.CarryFromDisk(new MainWindow.AppSettings { UseBuiltInPicker = onDisk }, save);
        Assert.Equal(onDisk, save.UseBuiltInPicker);
        MainWindow.CarryFromDisk(null, save = new MainWindow.AppSettings { UseBuiltInPicker = inStaleWindow });
        Assert.Equal(inStaleWindow, save.UseBuiltInPicker);   // no file yet: the window's own value stands
    }

    [Fact]
    public void TheNoteSaysHowToSwitchBackOnlyWhileTheSettingIsOn()
    {
        Assert.Equal("Markdown Midget's own file picker. To use Windows' dialog again: Edit ▸ Settings ▸ File dialogs, then restart Markdown Midget.",
            FilePickerModel.NoteText(settingOn: true));
        Assert.Equal("Markdown Midget's own file picker.", FilePickerModel.NoteText(settingOn: false));   // a one-off fallback
    }
}
