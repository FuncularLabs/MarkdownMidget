namespace MarkdownMidget.Chrome;

/// <summary>
/// The palette keys the windows use, for <c>{DynamicResource {x:Static chrome:ChromeKeys.X}}</c>:
/// a misspelt member stops the build, where a misspelt string would quietly paint nothing.
/// ChromePaletteTests checks each one is in both palettes.
/// </summary>
public static class ChromeKeys
{
    public const string TextAccent = "Chrome.Text.Accent";
    public const string TextSecondary = "Chrome.Text.Secondary";
    public const string BusyVeil = "Chrome.Busy.Veil";
    public const string SplashBackground = "Chrome.Splash.Background";
    public const string FindStatusBackground = "Chrome.Find.Status.Background";
    public const string FindStatusBorder = "Chrome.Find.Status.Border";
    public const string DialogPathText = "Chrome.Dialog.PathText";
    public const string DialogMutedText = "Chrome.Dialog.MutedText";
    public const string DialogCodeText = "Chrome.Dialog.CodeText";
    public const string DialogHintText = "Chrome.Dialog.HintText";
    public const string DialogFaintText = "Chrome.Dialog.FaintText";
    public const string DialogWarningText = "Chrome.Dialog.WarningText";
    public const string DialogErrorText = "Chrome.Dialog.ErrorText";
    public const string DialogInvalidText = "Chrome.Dialog.InvalidText";
    public const string ChipBackground = "Chrome.Chip.Background";
    public const string ChipBorder = "Chrome.Chip.Border";
    public const string ChipText = "Chrome.Chip.Text";
    public const string ChipArrowText = "Chrome.Chip.ArrowText";
    public const string ChipCaptionText = "Chrome.Chip.CaptionText";
    public const string ChipRemovedText = "Chrome.Chip.RemovedText";
    public const string IconNumberedList = "Chrome.Icon.NumberedList";
    public const string IconSpellCheck = "Chrome.Icon.SpellCheck";
    /// <summary>A style, not a brush: the built-in file picker's glyph buttons.</summary>
    public const string PickerNavButton = "Chrome.Picker.NavButton";
}
