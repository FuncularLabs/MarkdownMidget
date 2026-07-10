using System.IO;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Smoke + security tests for the document image-serving path (the
/// <c>mdm-doc.invalid</c> virtual host added in 0.4.0). The containment logic is
/// the boundary that keeps a document's asset references from escaping its own
/// folder, so it gets explicit traversal coverage.
/// </summary>
public class DocAssetTests
{
    private const string Root = @"C:\docs";

    // ---- happy path: assets inside the document folder resolve ----

    [Fact]
    public void ResolveRelative_FileInRoot_Resolves()
    {
        Assert.Equal(@"C:\docs\logo.png", DocAsset.ResolveRelative(Root, "logo.png"));
    }

    [Fact]
    public void ResolveRelative_FileInSubfolder_Resolves()
    {
        Assert.Equal(@"C:\docs\img\sub\pic.png",
            DocAsset.ResolveRelative(Root, @"img\sub\pic.png"));
    }

    [Fact]
    public void ResolveRelative_EmptyRel_ReturnsRootItself()
    {
        Assert.Equal(@"C:\docs", DocAsset.ResolveRelative(Root, ""));
    }

    // ---- security: nothing outside the document subtree is served ----

    [Fact]
    public void ResolveRelative_ParentTraversal_Rejected()
    {
        Assert.Null(DocAsset.ResolveRelative(Root, @"..\secret.png"));
    }

    [Fact]
    public void ResolveRelative_DeepParentTraversal_Rejected()
    {
        Assert.Null(DocAsset.ResolveRelative(Root, @"..\..\..\Windows\win.ini"));
    }

    [Fact]
    public void ResolveRelative_SiblingSharingNamePrefix_Rejected()
    {
        // C:\docs-secret must not be reachable from a C:\docs root even though it
        // shares the "docs" prefix — this is why the check uses root + separator,
        // not a bare StartsWith(root).
        Assert.Null(DocAsset.ResolveRelative(Root, @"..\docs-secret\x.png"));
    }

    [Fact]
    public void ResolveRelative_AbsoluteRel_Rejected()
    {
        // Path.Combine discards the root when the second arg is rooted; the
        // containment check must still reject the resulting outside path.
        Assert.Null(DocAsset.ResolveRelative(Root, @"C:\Windows\evil.png"));
    }

    [Fact]
    public void ResolveRelative_NullRoot_ReturnsNull()
    {
        Assert.Null(DocAsset.ResolveRelative(null, "logo.png"));
    }

    // ---- URI parsing (the form the WebView2 handler actually receives) ----

    [Fact]
    public void ResolveWithinRoot_SimpleUri_Resolves()
    {
        Assert.Equal(@"C:\docs\logo.png",
            DocAsset.ResolveWithinRoot(Root, "https://mdm-doc.invalid/logo.png"));
    }

    [Fact]
    public void ResolveWithinRoot_SubfolderUri_Resolves()
    {
        Assert.Equal(@"C:\docs\img\sub\pic.jpg",
            DocAsset.ResolveWithinRoot(Root, "https://mdm-doc.invalid/img/sub/pic.jpg"));
    }

    [Fact]
    public void ResolveWithinRoot_PercentEncodedSpace_Unescapes()
    {
        Assert.Equal(@"C:\docs\my image.png",
            DocAsset.ResolveWithinRoot(Root, "https://mdm-doc.invalid/my%20image.png"));
    }

    [Fact]
    public void ResolveWithinRoot_PercentEncodedSeparatorTraversal_Rejected()
    {
        // %2f survives Uri parsing (unlike %2e%2e, which Uri collapses to root),
        // so ..%2f..%2f decodes to ../../ and must be caught by the containment
        // check. This is THE real encoded-traversal vector for this host.
        Assert.Null(DocAsset.ResolveWithinRoot(
            Root, "https://mdm-doc.invalid/..%2f..%2fsecret.png"));
    }

    [Fact]
    public void ResolveWithinRoot_EncodedDotSegments_StayInsideRoot()
    {
        // Documents the (safe) counterpart: encoded dot-segments are normalized
        // away by Uri to a path inside the folder — not an escape.
        Assert.Equal(@"C:\docs\secret.png",
            DocAsset.ResolveWithinRoot(Root, "https://mdm-doc.invalid/%2e%2e/%2e%2e/secret.png"));
    }

    [Fact]
    public void ResolveWithinRoot_NullRoot_ReturnsNull()
    {
        Assert.Null(DocAsset.ResolveWithinRoot(null, "https://mdm-doc.invalid/logo.png"));
    }

    // ---- MIME mapping ----

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".PNG", "image/png")]     // case-insensitive
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".svg", "image/svg+xml")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".bmp", "image/bmp")]
    [InlineData(".ico", "image/x-icon")]
    [InlineData(".avif", "image/avif")]
    [InlineData(".exe", "application/octet-stream")]  // unknown → safe default
    [InlineData("", "application/octet-stream")]
    public void ContentTypeFor_MapsExtension(string ext, string expected)
    {
        Assert.Equal(expected, DocAsset.ContentTypeFor(ext));
    }
}
