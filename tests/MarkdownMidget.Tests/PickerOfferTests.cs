using System.Text.Json;
using MarkdownMidget.Picker;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Why the built-in picker is on, and the one-time offer of Windows' dialog (#12, item 6).
/// Versions before 1.0.0-rc3 switched the picker on by mistake when Cancel was pressed, and
/// recorded nothing; from this version every switch records its reason, so a setting that is
/// on with no reason is the one case worth asking about.
/// </summary>
public class PickerOfferTests
{
    private static readonly DateTime When = new(2026, 10, 8, 18, 40, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, null, false, true)]       // on, no reason, never asked: the old Cancel bug's state
    [InlineData(true, null, true, false)]
    [InlineData(true, "crash", false, false)]
    [InlineData(true, "user", false, false)]
    [InlineData(true, "crash", true, false)]
    [InlineData(false, null, false, false)]
    [InlineData(false, "user", false, false)]
    [InlineData(false, null, true, false)]
    public void AsksOnlyWhenTheBuiltInPickerIsOnWithNoReasonAndTheQuestionWasNeverAnswered(bool on, string? reason, bool answered, bool ask) =>
        Assert.Equal(ask, PickerOffer.ShouldAsk(on, reason is null ? null : PickerOffer.Record(reason, "1.0.1", When), answered));

    [Fact]
    public void ARecordWithoutAReasonCountsAsNone() =>
        Assert.True(PickerOffer.ShouldAsk(true, new PickerSwitch { Version = "1.0.1", Utc = When }, false));

    [Theory]
    [InlineData(true, "crash")]
    [InlineData(true, "user")]
    [InlineData(false, "user")]
    public void EverySwitchRecordsItsReasonVersionAndTime(bool on, string reason)
    {
        var s = new MainWindow.AppSettings { UseBuiltInPicker = !on };
        PickerOffer.Switch(s, on, reason, "1.0.1", When);
        Assert.Equal(on, s.UseBuiltInPicker);
        Assert.Equal((reason, "1.0.1", When), (s.BuiltInPickerSwitch!.Reason, s.BuiltInPickerSwitch.Version, s.BuiltInPickerSwitch.Utc));
        Assert.False(s.BuiltInPickerOfferAnswered);
    }

    [Fact]
    public void KeepingTheBuiltInPickerRecordsItAsTheUsersChoice()
    {
        // The last answer wins: a Keep in one window after a Use in another brings the built-in picker back.
        var s = new MainWindow.AppSettings { UseBuiltInPicker = false };
        PickerOffer.Answer(s, useWindows: false, "1.0.1", When);
        Assert.Equal((true, true, "user", When), (s.UseBuiltInPicker, s.BuiltInPickerOfferAnswered, s.BuiltInPickerSwitch?.Reason, s.BuiltInPickerSwitch?.Utc));
    }

    [Theory]
    [InlineData(true, true, false, false, false)]   // the offer, from Import in the dialog, chose Windows': OK keeps that
    [InlineData(false, false, true, false, true)]   // Windows' dialog crashed during Import: OK keeps the switch
    [InlineData(true, true, true, false, true)]     // nothing changed
    [InlineData(true, false, true, true, false)]    // the box cleared
    [InlineData(false, true, false, true, true)]    // the box ticked
    [InlineData(true, false, false, true, false)]   // cleared after the offer cleared it too: saved as the user's
    public void OkInSettingsChangesTheSettingOnlyWhenTheBoxWasChanged(bool shown, bool chosen, bool now, bool save, bool after) =>
        Assert.Equal((save, after), PickerOffer.AfterSettings(shown, chosen, now));

    [Fact]
    public void ACrashSwitchesWithTheReasonCrash() =>
        Assert.Equal((PickerOffer.Crash, PickerOffer.AppVersion, When),
            (FilePickerService.CrashSwitch(When).Reason, FilePickerService.CrashSwitch(When).Version, FilePickerService.CrashSwitch(When).Utc));

    [Fact]
    public void AnOfferThatFailsIsLoggedAndCountsAsNoAnswer()
    {
        Exception? logged = null;
        Assert.False(FilePickerService.OfferAnswer(() => throw new InvalidOperationException("no window"), ex => logged = ex));
        Assert.IsType<InvalidOperationException>(logged);
        Assert.True(FilePickerService.OfferAnswer(() => true, _ => throw new InvalidOperationException("not logged")));
    }

    [Fact]
    public void UsingWindowsDialogTurnsTheSettingOffAsTheUsersChoice()
    {
        var s = new MainWindow.AppSettings { UseBuiltInPicker = true };
        PickerOffer.Answer(s, useWindows: true, "1.0.1", When);
        Assert.Equal((false, true, "user"), (s.UseBuiltInPicker, s.BuiltInPickerOfferAnswered, s.BuiltInPickerSwitch?.Reason));
    }

    [Fact]
    public void AnotherWindowsUnrelatedSaveKeepsTheReasonAndTheAnswer()
    {
        // Window A answered the offer, or crashed and switched; window B, launched before, toggles
        // word wrap. B holds neither field, and must neither erase them nor roll them back.
        var disk = new MainWindow.AppSettings { BuiltInPickerOfferAnswered = true };
        PickerOffer.Switch(disk, true, PickerOffer.Crash, "1.0.1", When);
        var save = new MainWindow.AppSettings { WordWrap = true };
        MainWindow.CarryFromDisk(disk, save);
        Assert.Equal((true, PickerOffer.Crash, When), (save.BuiltInPickerOfferAnswered, save.BuiltInPickerSwitch?.Reason, save.BuiltInPickerSwitch?.Utc));
    }

    [Fact]
    public void TheReasonAndTheAnswerSurviveSettingsJsonAndAnOlderFileHasNeither()
    {
        var s = new MainWindow.AppSettings { BuiltInPickerOfferAnswered = true };
        PickerOffer.Switch(s, true, PickerOffer.User, "1.0.1", When);
        var back = JsonSerializer.Deserialize<MainWindow.AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.Equal((true, "user", "1.0.1", When), (back.BuiltInPickerOfferAnswered, back.BuiltInPickerSwitch?.Reason, back.BuiltInPickerSwitch?.Version, back.BuiltInPickerSwitch?.Utc));
        var old = JsonSerializer.Deserialize<MainWindow.AppSettings>("{\"UseBuiltInPicker\":true}")!;
        Assert.True(PickerOffer.ShouldAsk(old.UseBuiltInPicker, old.BuiltInPickerSwitch, old.BuiltInPickerOfferAnswered));
    }

    [Theory]
    [InlineData(false, false, false)]   // off: the question is never even considered
    [InlineData(true, false, true)]     // kept
    [InlineData(true, true, false)]     // Windows' dialog, this time and from now on
    public void OnlyAnOpenBuiltInPickerAsksAndUsingWindowsTurnsItOffForThisPick(bool on, bool useWindows, bool builtInNow)
    {
        var asked = false;
        Assert.Equal(builtInNow, FilePickerService.BuiltInAfterOffer(on, () => { asked = true; return useWindows; }));
        Assert.Equal(on, asked);
    }

    [Fact]
    public void TheCrashLogSaysWhyAndWhenThePickerWasSwitchedOn()
    {
        var findings = new PickerCrashFindings(unchecked((int)0xC0000005), null, FaultKind.None, []);
        var text = PickerCrashClues.Details(findings, "1.0.1", "os", PickerOffer.Record(PickerOffer.Crash, "1.0.1", When));
        Assert.Contains("The built-in file picker was switched on: reason crash, by Markdown Midget 1.0.1, at 2026-10-08 18:40:00 UTC", text);
        Assert.DoesNotContain("switched on", PickerCrashClues.Details(findings, "1.0.1", "os"));
    }
}
