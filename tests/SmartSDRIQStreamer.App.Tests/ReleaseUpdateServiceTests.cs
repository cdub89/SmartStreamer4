using System.Text.Json;
using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Tag ranking and release selection behind the in-app update check. The
/// preview convention (vX.Y.Z-previewN tester builds, adopted 2026-09-08)
/// depends on GA outranking every preview at the same numeric version and on
/// GitHub pre-releases never reaching operators.
/// </summary>
public class ReleaseUpdateServiceTests
{
    [Theory]
    [InlineData("v0.3.2", "v0.3.2-preview1")]
    [InlineData("v0.3.2", "v0.3.2-preview3")]
    [InlineData("v0.3.2-preview2", "v0.3.2-preview1")]
    [InlineData("v0.3.2-preview1", "v0.3.1")]
    [InlineData("v0.3.2", "v0.3.1")]
    [InlineData("v0.3.2", "v0.3.2b5")]
    public void Left_tag_outranks_right_tag(string left, string right)
    {
        Assert.True(ReleaseUpdateService.CompareTags(left, right) > 0);
        Assert.True(ReleaseUpdateService.CompareTags(right, left) < 0);
    }

    [Theory]
    [InlineData("v0.3.2", "0.3.2")]
    [InlineData("v0.3.2-preview1", "0.3.2-preview1")]
    [InlineData("v0.3.2-preview1", "v0.3.2-Preview1")]
    public void Equivalent_tags_compare_equal(string left, string right)
    {
        Assert.Equal(0, ReleaseUpdateService.CompareTags(left, right));
    }

    [Fact]
    public void Unparseable_tag_yields_no_comparison()
    {
        Assert.Null(ReleaseUpdateService.CompareTags("v0.3", "v0.3.2"));
    }

    [Fact]
    public void Prerelease_entries_are_skipped_in_favor_of_the_newest_ga()
    {
        // GitHub lists newest first; a pre-release ahead of GA must not win.
        var result = Evaluate("v0.3.1",
            """
            [
              { "tag_name": "v0.3.2-preview1", "prerelease": true, "draft": false, "html_url": "https://example/preview" },
              { "tag_name": "v0.3.1", "prerelease": false, "draft": false, "html_url": "https://example/ga" }
            ]
            """);

        Assert.True(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("v0.3.1", result.LatestTag);
        Assert.Equal("https://example/ga", result.LatestReleaseUrl);
    }

    [Fact]
    public void Draft_entries_are_skipped()
    {
        var result = Evaluate("v0.3.1",
            """
            [
              { "tag_name": "v0.3.3", "draft": true, "html_url": "https://example/draft" },
              { "tag_name": "v0.3.2", "draft": false, "html_url": "https://example/ga" }
            ]
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v0.3.2", result.LatestTag);
    }

    [Fact]
    public void Preview_tester_is_prompted_when_ga_ships()
    {
        var result = Evaluate("v0.3.2-preview2",
            """
            [ { "tag_name": "v0.3.2", "html_url": "https://example/ga" } ]
            """);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v0.3.2", result.LatestTag);
    }

    [Fact]
    public void Only_prereleases_means_no_published_release()
    {
        var result = Evaluate("v0.3.1",
            """
            [ { "tag_name": "v0.3.2-preview1", "prerelease": true } ]
            """);

        Assert.False(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void Non_array_payload_is_rejected()
    {
        var result = Evaluate("v0.3.1", """{ "message": "rate limited" }""");

        Assert.False(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
    }

    private static ReleaseCheckResult Evaluate(string currentTag, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReleaseUpdateService.EvaluateReleases(doc.RootElement, currentTag);
    }
}
