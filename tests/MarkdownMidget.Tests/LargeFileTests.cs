using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>When an open offers the source view (<see cref="LargeFile"/>). That the overlay shows it is a manual check.</summary>
public class LargeFileTests
{
    [Fact]
    public void The_threshold_is_250_KB() => Assert.Equal(250L * 1024, LargeFile.HintBytes);

    [Theory]
    [InlineData(0L, false)]
    [InlineData(250L * 1024 - 1, false)]
    [InlineData(250L * 1024, true)]
    [InlineData(512L * 1024, true)]
    public void A_file_opening_formatted_is_offered_the_source_view_at_or_over_the_threshold(long bytes, bool hint) =>
        Assert.Equal(hint, LargeFile.ShouldHint(bytes, openingInSource: false));

    [Theory]
    [InlineData(250L * 1024)]
    [InlineData(512L * 1024)]
    public void A_file_opening_into_the_source_view_is_never_offered_it(long bytes) =>
        Assert.False(LargeFile.ShouldHint(bytes, openingInSource: true));
}
