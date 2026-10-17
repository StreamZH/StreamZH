// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamZH.Core;

namespace StreamZH.Host.General;

/// <param name="Checked">When the last check ran; null = never since the start.</param>
/// <param name="Message">What to tell the user when there is no update information.</param>
public sealed record UpdateCheckResult(DateTimeOffset? Checked, string? LatestVersion, DateTimeOffset? Published, string? ReleaseUrl, bool UpdateAvailable, string? Message);

/// <summary>
/// "Check for updates": asks GitHub for the latest release of StreamZH. Only a check: StreamZH does not install updates
/// itself yet. The repository is private until 1.0, so GitHub answers 404 and the check says so instead of failing.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory http, TimeProvider time, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "updates";
    public const string Repository = "streamzh/streamzh";
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private UpdateCheckResult _last = new(null, null, null, null, false, null);

    public UpdateCheckResult Last => _last;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        UpdateCheckResult result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result = new UpdateCheckResult(now, null, null, null, false, "No update information yet: StreamZH has no public releases until 1.0.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                result = new UpdateCheckResult(now, null, null, null, false, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            }
            else
            {
                var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
                var latest = release?.TagName?.TrimStart('v', 'V');
                var newer = latest is not null && IsNewer(latest, AppInfo.Version);
                result = new UpdateCheckResult(now, latest, release?.PublishedAt, release?.HtmlUrl, newer, latest is null ? "GitHub sent no version." : null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation("Update check failed: {Message}", ex.Message);
            result = new UpdateCheckResult(now, null, null, null, false, $"Could not reach GitHub: {ex.Message}");
        }

        _last = result;
        return result;
    }

    /// <summary>"0.7.0" is newer than "0.6.2"; pre-release suffixes are ignored.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Core(candidate), out var a) && Version.TryParse(Core(current), out var b) && a > b;

    private static string Core(string version)
    {
        var core = version.Split('-', '+')[0];
        return core.Count(c => c == '.') == 0 ? core + ".0" : core;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt);
}
