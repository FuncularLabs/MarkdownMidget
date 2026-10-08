using System.Windows;

namespace MarkdownMidget.Picker;

/// <summary>The one-time question before a file dialog (<see cref="PickerOffer"/>). Only the
/// answer is here; whether to ask, and the record of the answer, are the caller's.</summary>
public partial class PickerOfferDialog : Window
{
    private PickerOfferDialog()
    {
        InitializeComponent();
        QuestionText.Text = PickerOffer.Question;
    }

    /// <summary>True for Use Windows' dialog; Keep, Esc and closing the window are all Keep.</summary>
    internal static bool Ask(Window owner) => new PickerOfferDialog { Owner = owner }.ShowDialog() == true;

    private void UseWindows_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
