using System.Linq;
using MarkdownMidget.Updates;
using Xunit;

namespace MarkdownMidget.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v0.5.1", "0.5.1", false)]
    [InlineData("0.6.0-beta1", "0.6.0-beta1", true)]
    [InlineData("V0.4.0-beta1", "0.4.0-beta1", true)]
    [InlineData("v0.6", "0.6.0", false)]           // normalized to 3 components
    public void Parse_Roundtrips(string input, string expected, bool prerelease)
    {
        var v = UpdateVersion.Parse(input);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.ToString());
        Assert.Equal(prerelease, v.IsPrerelease);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v-beta1")]
    public void Parse_RejectsJunk(string? input) => Assert.Null(UpdateVersion.Parse(input));

    // The ordering rules the update offer depends on.
    [Theory]
    [InlineData("0.5.1", "0.5.0", 1)]              // numeric wins
    [InlineData("0.6.0-beta1", "0.5.1", 1)]        // newer prerelease > older stable
    [InlineData("0.6.0", "0.6.0-beta1", 1)]        // stable > its own prerelease
    [InlineData("0.6.0-beta2", "0.6.0-beta1", 1)]  // beta2 > beta1
    [InlineData("0.6.0-rc1", "0.6.0-beta9", 1)]    // rc > beta (label ordinal)
    [InlineData("0.5.1", "0.5.1", 0)]
    public void CompareTo_Orders(string a, string b, int sign)
    {
        var va = UpdateVersion.Parse(a)!;
        var vb = UpdateVersion.Parse(b)!;
        Assert.Equal(sign, System.Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-sign, System.Math.Sign(vb.CompareTo(va)));
    }
}

public class ReleaseFeedTests
{
    private static string Release(string tag, bool prerelease, bool draft = false, bool withAsset = true)
    {
        var assets = withAsset
            ? $@"[{{""name"":""MarkdownMidget-{tag}-win-x64-net10.exe"",
                  ""browser_download_url"":""https://example.com/{tag}.exe"",""size"":100}}]"
            : "[]";
        return $@"{{""tag_name"":""{tag}"",""prerelease"":{prerelease.ToString().ToLower()},
                  ""draft"":{draft.ToString().ToLower()},""html_url"":""https://example.com/{tag}"",
                  ""assets"":{assets}}}";
    }

    private static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Select_PicksNewestOfEachChannel_ByVersionNotListOrder()
    {
        // Deliberately out of order: the API returns newest-first normally, but we
        // must not depend on that.
        var json = Feed(
            Release("v0.4.1", prerelease: false),
            Release("v0.6.0-beta1", prerelease: true),
            Release("v0.5.1", prerelease: false),
            Release("v0.5.0-beta1", prerelease: true));
        var check = ReleaseFeed.Select(json);
        Assert.Equal("v0.5.1", check.Stable?.Tag);
        Assert.Equal("v0.6.0-beta1", check.PrereleaseRelease?.Tag);
    }

    [Fact]
    public void Select_IgnoresDraftsAndAssetlessReleases()
    {
        var json = Feed(
            Release("v0.9.0", prerelease: false, draft: true),          // draft: invisible
            Release("v0.8.0", prerelease: false, withAsset: false),     // no exe: not installable
            Release("v0.5.1", prerelease: false));
        var check = ReleaseFeed.Select(json);
        Assert.Equal("v0.5.1", check.Stable?.Tag);
    }

    [Fact]
    public void Select_EmptyFeed_YieldsNulls()
    {
        var check = ReleaseFeed.Select("[]");
        Assert.Null(check.Stable);
        Assert.Null(check.PrereleaseRelease);
    }

    [Fact]
    public void Select_CarriesAssetDetails()
    {
        var check = ReleaseFeed.Select(Feed(Release("v0.5.1", prerelease: false)));
        Assert.Equal("MarkdownMidget-v0.5.1-win-x64-net10.exe", check.Stable?.AssetName);
        Assert.Equal(100, check.Stable?.AssetSize);
        Assert.StartsWith("https://example.com/", check.Stable?.AssetUrl);
    }
}
