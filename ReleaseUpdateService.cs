using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SDRIQStreamer.App;

public interface IReleaseUpdateService
{
    Task<ReleaseCheckResult> CheckForUpdateAsync(string currentTag, CancellationToken ct = default);
}

public sealed record ReleaseCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    string CurrentTag,
    string LatestTag,
    string LatestReleaseUrl,
    string StatusMessage);

public sealed class ReleaseUpdateService : IReleaseUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/cdub89/SmartStreamer4/releases?per_page=10";
    private static readonly HttpClient s_httpClient = BuildHttpClient();

    public async Task<ReleaseCheckResult> CheckForUpdateAsync(string currentTag, CancellationToken ct = default)
    {
        var normalizedCurrent = NormalizeTag(currentTag);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
        {
            return new ReleaseCheckResult(
                Succeeded: false,
                IsUpdateAvailable: false,
                CurrentTag: currentTag,
                LatestTag: string.Empty,
                LatestReleaseUrl: string.Empty,
                StatusMessage: "Current app version is unavailable.");
        }

        try
        {
            using var response = await s_httpClient.GetAsync(ReleasesApiUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ReleaseCheckResult(
                    Succeeded: false,
                    IsUpdateAvailable: false,
                    CurrentTag: normalizedCurrent,
                    LatestTag: string.Empty,
                    LatestReleaseUrl: string.Empty,
                    StatusMessage: $"GitHub update check failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(payload);
            return EvaluateReleases(doc.RootElement, normalizedCurrent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ReleaseCheckResult(
                Succeeded: false,
                IsUpdateAvailable: false,
                CurrentTag: normalizedCurrent,
                LatestTag: string.Empty,
                LatestReleaseUrl: string.Empty,
                StatusMessage: $"GitHub update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the GitHub releases list (newest first) and reports whether the
    /// first publishable entry outranks <paramref name="normalizedCurrent"/>.
    /// Internal so the selection rules are unit-testable without HTTP.
    /// </summary>
    internal static ReleaseCheckResult EvaluateReleases(JsonElement releases, string normalizedCurrent)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return new ReleaseCheckResult(
                Succeeded: false,
                IsUpdateAvailable: false,
                CurrentTag: normalizedCurrent,
                LatestTag: string.Empty,
                LatestReleaseUrl: string.Empty,
                StatusMessage: "GitHub update response format was unexpected.");
        }

        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object)
                continue;

            if (IsFlagSet(release, "draft"))
                continue;

            // Pre-releases are skipped so a GitHub pre-release can never prompt
            // GA operators. Builds fielded before 2026-09-08 lack this check, so
            // the release script still refuses to publish preview tags at all;
            // this guard only makes a future pre-release channel safe once every
            // client has moved past those builds.
            if (IsFlagSet(release, "prerelease"))
                continue;

            var latestTag = release.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString() ?? string.Empty
                : string.Empty;
            var latestUrl = release.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString() ?? string.Empty
                : string.Empty;

            var normalizedLatest = NormalizeTag(latestTag);
            if (string.IsNullOrWhiteSpace(normalizedLatest))
                continue;

            var compare = CompareTags(normalizedLatest, normalizedCurrent);
            if (!compare.HasValue)
            {
                return new ReleaseCheckResult(
                    Succeeded: false,
                    IsUpdateAvailable: false,
                    CurrentTag: normalizedCurrent,
                    LatestTag: normalizedLatest,
                    LatestReleaseUrl: latestUrl,
                    StatusMessage: $"Unable to compare versions ({normalizedCurrent} vs {normalizedLatest}).");
            }

            var updateAvailable = compare.Value > 0;
            var status = updateAvailable
                ? $"Update available: {normalizedLatest} (current: {normalizedCurrent})."
                : $"Up to date ({normalizedCurrent}).";

            return new ReleaseCheckResult(
                Succeeded: true,
                IsUpdateAvailable: updateAvailable,
                CurrentTag: normalizedCurrent,
                LatestTag: normalizedLatest,
                LatestReleaseUrl: latestUrl,
                StatusMessage: status);
        }

        return new ReleaseCheckResult(
            Succeeded: false,
            IsUpdateAvailable: false,
            CurrentTag: normalizedCurrent,
            LatestTag: string.Empty,
            LatestReleaseUrl: string.Empty,
            StatusMessage: "No published GitHub releases were found.");
    }

    private static bool IsFlagSet(JsonElement release, string propertyName) =>
        release.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static HttpClient BuildHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartStreamer4/1.0 (+https://github.com/cdub89/SmartStreamer4)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string NormalizeTag(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
            return string.Empty;

        var trimmed = rawTag.Trim();
        if (trimmed.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
            return "v" + trimmed[2..];

        if (!trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return $"v{trimmed}";

        return $"v{trimmed[1..]}";
    }

    internal static int? CompareTags(string left, string right)
    {
        if (!TryParseTag(left, out var leftVersion) || !TryParseTag(right, out var rightVersion))
            return null;

        var numericCompare = leftVersion.Numeric.CompareTo(rightVersion.Numeric);
        if (numericCompare != 0)
            return numericCompare;

        var channelCompare = leftVersion.ChannelRank.CompareTo(rightVersion.ChannelRank);
        if (channelCompare != 0)
            return channelCompare;

        return leftVersion.ChannelNumber.CompareTo(rightVersion.ChannelNumber);
    }

    private static bool TryParseTag(string tag, out ParsedTag parsed)
    {
        parsed = default;
        var normalized = NormalizeTag(tag);
        if (normalized.Length < 2)
            return false;

        var body = normalized[1..];
        var match = Regex.Match(body, @"^(?<maj>\d+)\.(?<min>\d+)\.(?<patch>\d+)(?<suffix>.*)$");
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["maj"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["min"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var suffix = match.Groups["suffix"].Value.Trim().ToLowerInvariant();
        suffix = suffix.TrimStart('-', '.', '_');
        var (rank, number) = ParseChannel(suffix);
        parsed = new ParsedTag(new Version(major, minor, patch), rank, number);
        return true;
    }

    private static (int Rank, int Number) ParseChannel(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return (3, 0); // stable

        var betaMatch = Regex.Match(suffix, @"^(b|beta)(?<n>\d*)$");
        if (betaMatch.Success)
            return (1, ParseOptionalNumber(betaMatch.Groups["n"].Value));

        // Numbered tester builds (vX.Y.Z-previewN, adopted 2026-09-08) take the
        // rank the retired bN suffix held: below the clean GA tag at the same
        // numeric version so preview testers are prompted when GA ships, and
        // ordered among themselves by N so preview2 outranks preview1.
        var previewMatch = Regex.Match(suffix, @"^preview(?<n>\d*)$");
        if (previewMatch.Success)
            return (1, ParseOptionalNumber(previewMatch.Groups["n"].Value));

        var rcMatch = Regex.Match(suffix, @"^(rc)(?<n>\d*)$");
        if (rcMatch.Success)
            return (2, ParseOptionalNumber(rcMatch.Groups["n"].Value));

        var alphaMatch = Regex.Match(suffix, @"^(a|alpha)(?<n>\d*)$");
        if (alphaMatch.Success)
            return (0, ParseOptionalNumber(alphaMatch.Groups["n"].Value));

        return (0, 0);
    }

    private static int ParseOptionalNumber(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    private readonly record struct ParsedTag(Version Numeric, int ChannelRank, int ChannelNumber);
}
