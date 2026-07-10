using System;
using System.IO;

namespace MarkdownMidget;

/// <summary>
/// Pure helpers for serving a document's local asset files (images) to the editor
/// via the <c>mdm-doc.invalid</c> virtual host. Kept free of any WebView2/WPF
/// dependency so the security-critical path logic can be unit-tested directly.
/// </summary>
internal static class DocAsset
{
    /// <summary>
    /// Resolve a doc-host request URI to an absolute file path inside
    /// <paramref name="root"/>, or <c>null</c> if the request escapes the document
    /// folder (path traversal), <paramref name="root"/> is null/empty, or the URI
    /// can't be parsed.
    /// </summary>
    /// <remarks>
    /// Both the browser and <see cref="System.Uri"/> collapse literal <em>and</em>
    /// percent-encoded <c>..</c> dot-segments before we ever combine paths — a
    /// request for <c>%2e%2e/x</c> arrives as just <c>/x</c>, safely inside the
    /// folder. What survives is a percent-encoded separator (<c>%2f</c>):
    /// <c>..%2f..%2fsecret.png</c> reaches us intact, <see
    /// cref="System.Uri.UnescapeDataString"/> turns it into <c>../../secret.png</c>,
    /// and only the containment check below rejects it — so this check is the real
    /// defense against traversal, not belt-and-suspenders. It compares against
    /// <c>root + separator</c> (not a bare <c>StartsWith(root)</c>) so a sibling
    /// folder sharing a name prefix (e.g. <c>docs</c> vs <c>docs-secret</c>) can't
    /// be served.
    /// </remarks>
    public static string? ResolveWithinRoot(string? root, string requestUri)
    {
        if (string.IsNullOrEmpty(root)) return null;

        string rel;
        try
        {
            rel = Uri.UnescapeDataString(new Uri(requestUri).AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
        }
        catch { return null; }

        return ResolveRelative(root, rel);
    }

    /// <summary>
    /// Core containment: resolve <paramref name="rel"/> under <paramref name="root"/>
    /// and return the absolute path only if it stays inside the root subtree;
    /// otherwise <c>null</c>. Rejects <c>..</c> escapes, rooted/absolute
    /// <paramref name="rel"/> values (which <see cref="Path.Combine(string,string)"/>
    /// would otherwise let jump anywhere), and sibling folders that merely share a
    /// name prefix with the root.
    /// </summary>
    public static string? ResolveRelative(string? root, string rel)
    {
        if (string.IsNullOrEmpty(root)) return null;

        string full, rootFull;
        try
        {
            rootFull = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(rootFull, rel));
        }
        catch { return null; }

        if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)) return full;
        if (full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return full;
        return null; // outside the document folder — don't serve
    }

    /// <summary>MIME type for an image file extension (dot included), or a safe
    /// binary default for anything unrecognized.</summary>
    public static string ContentTypeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };
}
