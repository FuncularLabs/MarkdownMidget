using System;
using System.IO;
using System.Linq;

namespace MarkdownMidget.Secure;

/// <summary>
/// The pure, testable half of the Secure Markdown UI: path shapes, dialog
/// filters, and the password strength readout. Everything here is string-in/
/// string-out so the dialogs and menu handlers stay thin.
/// </summary>
internal static class SecureUi
{
    public static bool IsEncryptedPath(string? path) =>
        path is not null &&
        string.Equals(Path.GetExtension(path), SecureMarkdownFormat.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>x\doc.md → x\doc.mdenc (used by File ▸ Encrypt).</summary>
    public static string EncryptedPathFor(string path) =>
        Path.ChangeExtension(path, SecureMarkdownFormat.Extension);

    /// <summary>x\doc.mdenc → x\doc.md (used by Convert to Unencrypted).</summary>
    public static string PlaintextPathFor(string path) =>
        Path.ChangeExtension(path, ".md");

    /// <summary>
    /// The Open dialog's filter. *.mdenc joins the Markdown group only when the
    /// user opted in (Settings) — the default stays exactly what it always was.
    /// Encrypted files remain openable regardless via "All files" or a typed name.
    /// </summary>
    public static string OpenFilter(bool includeEncrypted) => includeEncrypted
        ? "Markdown (*.md;*.markdown;*.mdenc)|*.md;*.markdown;*.mdenc|Text (*.txt)|*.txt|All files (*.*)|*.*"
        : "Markdown (*.md;*.markdown)|*.md;*.markdown|Text (*.txt)|*.txt|All files (*.*)|*.*";

    /// <summary>Save As always offers both — choosing Secure Markdown IS the second
    /// path to encryption, per the design.</summary>
    public const string SaveFilter =
        "Markdown (*.md)|*.md|Secure Markdown (*.mdenc)|*.mdenc|Text (*.txt)|*.txt|All files (*.*)|*.*";

    /// <summary>1-based index of the Secure Markdown entry in SaveFilter.</summary>
    public const int SaveFilterEncryptedIndex = 2;

    /// <summary>
    /// A coarse, honest strength readout for the set-password dialog. Not a
    /// cracking-time estimate — those overpromise — just the three bands users
    /// act on. Length dominates deliberately: "correct horse battery staple"
    /// must not score below "P@ss1".
    /// </summary>
    public static string DescribeStrength(string password)
    {
        if (password.Length == 0) return "";
        var classes = (password.Any(char.IsLower) ? 1 : 0)
                    + (password.Any(char.IsUpper) ? 1 : 0)
                    + (password.Any(char.IsDigit) ? 1 : 0)
                    + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (password.Length >= 16 || (password.Length >= 12 && classes >= 3)) return "Strength: strong";
        if (password.Length >= 10 || (password.Length >= 8 && classes >= 3)) return "Strength: fair";
        return "Strength: weak — longer is what helps most";
    }
}
