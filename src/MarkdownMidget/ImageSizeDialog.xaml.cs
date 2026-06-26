using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace MarkdownMidget;

/// <summary>
/// Image resize prompt. Width and height stay locked to the image's original aspect
/// ratio — editing one recomputes the other.
/// </summary>
public partial class ImageSizeDialog : Window
{
    private readonly double _ratio; // width / height
    private bool _sync;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }

    public ImageSizeDialog(int curW, int curH, int natW, int natH)
    {
        InitializeComponent();
        // Aspect from the natural size when known, else the current size.
        _ratio = natW > 0 && natH > 0 ? (double)natW / natH
               : curH > 0 ? (double)curW / curH : 1.0;
        CurrentLabel.Text = $"Current size: {curW} × {curH} px";
        _sync = true;
        WidthBox.Text = curW.ToString();
        HeightBox.Text = curH.ToString();
        _sync = false;
        Loaded += (_, _) => { WidthBox.Focus(); WidthBox.SelectAll(); };
    }

    private void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    private void WidthChanged(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        if (!int.TryParse(WidthBox.Text, out var w) || w <= 0) return;
        _sync = true;
        HeightBox.Text = System.Math.Max(1, (int)System.Math.Round(w / _ratio)).ToString();
        _sync = false;
    }

    private void HeightChanged(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        if (!int.TryParse(HeightBox.Text, out var h) || h <= 0) return;
        _sync = true;
        WidthBox.Text = System.Math.Max(1, (int)System.Math.Round(h * _ratio)).ToString();
        _sync = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewWidth = int.TryParse(WidthBox.Text, out var w) && w > 0 ? w : 1;
        NewHeight = int.TryParse(HeightBox.Text, out var h) && h > 0 ? h : 1;
        DialogResult = true;
    }
}
