// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamZH.Core;
using StreamZH.Host.Service;

namespace StreamZH.Host.General;

/// <param name="Checked">When the last check ran; null = never since the start.</param>
/// <param name="Message">What to tell the user when there is no update information.</param>
/// <param name="AssetName">The file of the release that fits this installation (platform, processor, package type); null when none fits (Docker: pull the image).</param>
/// <param name="AssetUrl">Its download link on github.com.</param>
public sealed record UpdateCheckResult(
    DateTimeOffset? Checked,
    string? LatestVersion,
    DateTimeOffset? Published,
    string? ReleaseUrl,
    bool UpdateAvailable,
    string? Message,
    string? AssetName = null,
    string? AssetUrl = null);

/// <summary>
/// "Check for updates": asks GitHub for the latest release of StreamZH (<see cref="AppInfo.Repository"/>) and names the
/// file that fits this installation. Only a check: StreamZH does not install updates itself. A repository without
/// releases answers 404, which the check reports instead of failing.
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory http, TimeProvider time, AppPaths paths, ILogger<UpdateChecker> logger)
{
    public const string HttpClientName = "updates";
    public const string Repository = AppInfo.Repository;
    public const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private UpdateCheckResult _last = new(null, null, null, null, false, null);

    public UpdateCheckResult Last => _last;

    /// <summary>How this copy was installed (<see cref="ReleaseAssets.InstallKind"/>): decides the update instructions and the file offered.</summary>
    public string InstallKind => ReleaseAssets.InstallKind(paths.AppDirectory, RunningAs.Mode);

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
                result = new UpdateCheckResult(now, null, null, null, false, "No update information yet: GitHub lists no release of StreamZH.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                result = new UpdateCheckResult(now, null, null, null, false, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            }
            else
            {
                var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
                var latest = release?.TagName?.TrimStart('v', 'V');
                if (latest is not null && !IsGitHubReleaseUrl(release?.HtmlUrl))
                {
                    // The link is shown as "Download"; only StreamZH's own releases on github.com are accepted.
                    logger.LogWarning("Update check: ignored a release whose link does not point to a release of {Repository} on github.com", Repository);
                    result = new UpdateCheckResult(now, null, null, null, false, "GitHub sent a release with an unexpected link; ignored.");
                }
                else
                {
                    var newer = latest is not null && IsNewer(latest, AppInfo.Version);
                    var asset = PickAsset(release?.Assets);
                    result = new UpdateCheckResult(now, latest, release?.PublishedAt, release?.HtmlUrl, newer, latest is null ? "GitHub sent no version." : null, asset?.Name, asset?.Url);
                }
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

    /// <summary>The release file for this platform, processor and package type; only links on github.com count.</summary>
    private ReleaseAsset? PickAsset(IReadOnlyList<GitHubAsset>? assets)
    {
        if (assets is null)
        {
            return null;
        }

        var suffix = ReleaseAssets.Suffix(ReleaseAssets.CurrentOs(), RuntimeInformation.ProcessArchitecture, InstallKind);
        var candidates = assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && IsGitHubReleaseUrl(a.DownloadUrl))
            .Select(a => new ReleaseAsset(a.Name!, a.DownloadUrl!));
        return ReleaseAssets.Pick(candidates, suffix);
    }

    /// <summary>
    /// An https link to a release of StreamZH's own repository on github.com: the release page
    /// (<c>/{Repository}/releases/tag/…</c>) or a release file (<c>/{Repository}/releases/download/…</c>). Another
    /// repository's release is refused (SV4 X-01), and so is a path with percent escapes (browsers turn %2e%2e into a
    /// step up).
    /// </summary>
    public static bool IsGitHubReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains('%', StringComparison.Ordinal);

    /// <summary>
    /// The update check always validates GitHub's certificate, also when Settings → General → Certificate validation is
    /// off (that setting is meant for the user's own servers).
    /// </summary>
    public static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None;

    /// <summary>The handler of the update client: the proxy filter adds the proxy, the certificate check stays strict.</summary>
    public static SocketsHttpHandler CreateHandler() =>
        new() { SslOptions = { RemoteCertificateValidationCallback = ValidateCertificate } };

    /// <summary>
    /// "0.7.0" is newer than "0.6.2"; for the same numbers a release is newer than its pre-release ("1.0.0" &gt;
    /// "1.0.0-rc.1") and pre-releases compare by their dot-separated parts ("rc.10" &gt; "rc.2").
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Core(candidate), out var a) || !Version.TryParse(Core(current), out var b))
        {
            return false;
        }

        if (a != b)
        {
            return a > b;
        }

        var (left, right) = (PreRelease(candidate), PreRelease(current));
        return (left, right) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => ComparePreRelease(left, right) > 0,
        };
    }

    private static string Core(string version)
    {
        var core = version.Split('-', '+')[0];
        return core.Count(c => c == '.') == 0 ? core + ".0" : core;
    }

    private static string? PreRelease(string version)
    {
        var withoutBuild = version.Split('+')[0];
        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 || dash == withoutBuild.Length - 1 ? null : withoutBuild[(dash + 1)..];
    }

    // Semantic versioning: numeric parts compare as numbers and sort before text; more parts win when all else is equal.
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var numberA = long.TryParse(a[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x);
            var numberB = long.TryParse(b[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var y);
            var order = (numberA, numberB) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
