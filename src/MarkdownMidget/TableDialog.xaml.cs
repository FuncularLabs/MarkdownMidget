using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>Insert-table prompt: columns, rows, and whether to include a header row.</summary>
public partial class TableDialog : Window
{
    public int Columns { get; private set; } = 3;
    public int Rows { get; private set; } = 4;
    public bool HeaderRow { get; private set; } = true;

    public TableDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { ColsBox.Focus(); ColsBox.SelectAll(); };
    }

    private void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Columns = Clamp(ColsBox.Text, 1, 20, 3);
        Rows = Clamp(RowsBox.Text, 1, 100, 4);
        HeaderRow = HeaderBox.IsChecked == true;
        DialogResult = true;
    }

    private static int Clamp(string text, int min, int max, int fallback)
        => int.TryParse(text, out var n) ? System.Math.Clamp(n, min, max) : fallback;
}
