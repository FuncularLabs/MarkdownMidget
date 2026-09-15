using System.Diagnostics;
using System.Windows;

namespace MarkdownMidget;

/// <summary>Asks before a link leaves the app, showing the address <see cref="LinkOpening"/> checked. Cancel is the default.</summary>
public partial class OpenLinkDialog : Window
{
    private OpenLinkDialog(string shown) { InitializeComponent(); UrlText.Text = shown; }
    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    public static void Confirm(Window owner, string? url, Action<string> note)
    {
        if (!LinkOpening.TryValidate(url, out var uri, out var shown)) { note(LinkOpening.RefusedNote); return; }
        if (new OpenLinkDialog(shown) { Owner = owner }.ShowDialog() != true) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { note($"Couldn't open the link: {ex.Message}"); }
    }
}
