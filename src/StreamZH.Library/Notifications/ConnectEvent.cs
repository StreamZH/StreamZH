// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Core.Media;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Library.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectEventType>))]
public enum ConnectEventType
{
    Grab,
    Import,
    Upgrade,
    Rename,
    DownloadFailure,
    HealthIssue,
    HealthRestored,
    Test,

    // Requests (0.10)
    RequestCreated,
    RequestApproved,
    RequestDeclined,
    RequestAvailable,
}

/// <summary>The movie or series an event is about.</summary>
/// <param name="Id">Library id; null for a test message.</param>
public sealed record ConnectMedia(
    int? Id,
    string Title,
    int Year,
    int TmdbId,
    string? ImdbId,
    int? TvdbId,
    string? Overview,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    string? BackdropPath,
    string? Folder)
{
    public string Display => Year > 0 ? $"{Title} ({Year.ToString(CultureInfo.InvariantCulture)})" : Title;

    /// <summary>Poster for services that show images (Discord, Gotify, ntfy, Kodi, Emby).</summary>
    public string? PosterUrl => TmdbImages.Url(PosterPath, "w500");

    public string? FanartUrl => TmdbImages.Url(BackdropPath, "w1280");
}

public sealed record ConnectEpisode(int Id, int SeasonNumber, int EpisodeNumber, string Title, string? AirDate, string? Overview);

/// <summary>The release (grab, failed download) or the release an import came from.</summary>
public sealed record ConnectRelease(string Title, string? Quality, string? ReleaseGroup, string? Indexer, long? Size);

public sealed record ConnectFile(string Path, string RelativePath, long Size);

/// <summary>A movie or series request (0.10).</summary>
/// <param name="Status">API name of the status, e.g. "pending" or "available".</param>
/// <param name="RequestedBy">Display name of the user, the Discord name of a guest, or null (API).</param>
/// <param name="Seasons">Requested seasons; null = all seasons or a movie.</param>
/// <param name="Reason">Why it was declined.</param>
/// <param name="Automatic">Approved at once (the user approves themselves).</param>
public sealed record ConnectRequest(
    int Id,
    string Status,
    string? RequestedBy,
    IReadOnlyList<int>? Seasons,
    string? Note,
    string? Reason,
    bool Automatic,
    string? Source)
{
    /// <summary>"Seasons 1, 2" / "All seasons"; null for movies.</summary>
    public string? SeasonText(bool isSeries) =>
        !isSeries ? null : Seasons is { Count: > 0 } list ? $"{(list.Count == 1 ? "Season" : "Seasons")} {string.Join(", ", list)}" : "All seasons";
}

/// <param name="Level">"warn" or "error" (status window levels).</param>
public sealed record ConnectHealth(string Key, string Level, string Title, string? Message, string? Link);

/// <summary>
/// Something to tell the media servers and notification services about. <see cref="Subject"/> and
/// <see cref="Summary"/> are the short English texts most services show.
/// </summary>
public sealed record ConnectEvent
{
    public required ConnectEventType Type { get; init; }

    public MediaType? MediaType { get; init; }

    public ConnectMedia? Media { get; init; }

    public IReadOnlyList<ConnectEpisode> Episodes { get; init; } = [];

    public ConnectRelease? Release { get; init; }

    public IReadOnlyList<ConnectFile> Files { get; init; } = [];

    /// <summary>Movie or series folder (import, rename).</summary>
    public string? Folder { get; init; }

    public bool IsUpgrade => Type == ConnectEventType.Upgrade;

    public int? DownloadId { get; init; }

    /// <summary>Error of a failed download.</summary>
    public string? Message { get; init; }

    public ConnectHealth? Health { get; init; }

    /// <summary>The request of a request event (0.10).</summary>
    public ConnectRequest? Request { get; init; }

    public bool IsRequest => Type is ConnectEventType.RequestCreated or ConnectEventType.RequestApproved or ConnectEventType.RequestDeclined or ConnectEventType.RequestAvailable;

    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;

    public bool IsSeries => MediaType == Core.Media.MediaType.Series;

    public string Subject => Type switch
    {
        ConnectEventType.Grab => Media is null ? "Release Grabbed" : IsSeries ? (Episodes.Count > 1 ? "Episodes Grabbed" : "Episode Grabbed") : "Movie Grabbed",
        ConnectEventType.Import => IsSeries ? (Episodes.Count > 1 ? "Episodes Downloaded" : "Episode Downloaded") : "Movie Downloaded",
        ConnectEventType.Upgrade => IsSeries ? (Episodes.Count > 1 ? "Episodes Upgraded" : "Episode Upgraded") : "Movie Upgraded",
        ConnectEventType.Rename => IsSeries ? "Series Renamed" : "Movie Renamed",
        ConnectEventType.DownloadFailure => "Download Failed",
        ConnectEventType.HealthIssue => Health?.Level == "error" ? "Health Error" : "Health Warning",
        ConnectEventType.HealthRestored => "Health Restored",
        ConnectEventType.RequestCreated => Request?.Automatic == true
            ? (IsSeries ? "Series Request Automatically Approved" : "Movie Request Automatically Approved")
            : (IsSeries ? "New Series Request" : "New Movie Request"),
        ConnectEventType.RequestApproved => IsSeries ? "Series Request Approved" : "Movie Request Approved",
        ConnectEventType.RequestDeclined => IsSeries ? "Series Request Declined" : "Movie Request Declined",
        ConnectEventType.RequestAvailable => IsSeries ? "Series Now Available" : "Movie Now Available",
        _ => "Test Notification",
    };

    /// <summary>One line: "Dune (2021) · WEBDL-1080p · GROUP", "Show (2008) · S01E02 Title · …", the health notice or the test text.</summary>
    public string Summary
    {
        get
        {
            return Type switch
            {
                ConnectEventType.HealthIssue or ConnectEventType.HealthRestored => Join(Health?.Title, Health?.Message),
                ConnectEventType.Test => "This is a test message from StreamZH.",
                ConnectEventType.DownloadFailure => Join(Media?.Display ?? Release?.Title, EpisodeText, Message ?? "Download failed"),
                ConnectEventType.Rename => Media?.Display ?? Folder ?? string.Empty,
                _ when IsRequest => Join(
                    Media?.Display,
                    Request?.SeasonText(IsSeries),
                    Request?.RequestedBy is { } by ? $"requested by {by}" : null,
                    Type == ConnectEventType.RequestDeclined ? Request?.Reason : null),
                _ => Join(Media?.Display ?? Release?.Title, EpisodeText, Release?.Quality, Release?.ReleaseGroup),
            };
        }
    }

    /// <summary>"S01E02 Gray Matter", "S01E01-E03" for several, null for movies.</summary>
    public string? EpisodeText
    {
        get
        {
            if (Episodes.Count == 0)
            {
                return null;
            }

            var ordered = Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            var first = ordered[0];
            var code = $"S{first.SeasonNumber:00}E{first.EpisodeNumber:00}";
            if (ordered.Count == 1)
            {
                return string.IsNullOrWhiteSpace(first.Title) ? code : $"{code} {first.Title}";
            }

            var last = ordered[^1];
            return last.SeasonNumber == first.SeasonNumber ? $"{code}-E{last.EpisodeNumber:00}" : $"{code}-S{last.SeasonNumber:00}E{last.EpisodeNumber:00}";
        }
    }

    public string? TmdbUrl => Media is { TmdbId: > 0 } m
        ? (IsSeries ? $"https://www.themoviedb.org/tv/{m.TmdbId}" : $"https://www.themoviedb.org/movie/{m.TmdbId}")
        : null;

    public string? ImdbUrl => Media?.ImdbId is { Length: > 0 } imdb ? $"https://www.imdb.com/title/{imdb}/" : null;

    public long? Size => Files.Count > 0 ? Files.Sum(f => f.Size) : Release?.Size;

    /// <summary>A test event with a sample movie, so every service shows what a real message looks like.</summary>
    public static ConnectEvent Test() => new()
    {
        Type = ConnectEventType.Test,
        MediaType = Core.Media.MediaType.Movie,
        Media = new ConnectMedia(
            null,
            "Big Buck Bunny",
            2008,
            10378,
            "tt1254207",
            null,
            "A large and lovable rabbit deals with three tiny bullies, led by a flying squirrel.",
            ["Animation", "Comedy"],
            null,
            null,
            null),
        Release = new ConnectRelease("Big.Buck.Bunny.2008.1080p.BluRay.x264-STREAMZH", "Bluray-1080p", "STREAMZH", "Test", 1_234_567_890),
    };

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        if (bytes <= 0)
        {
            return "0 B";
        }

        var exponent = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        var value = bytes / Math.Pow(1024, exponent);
        return $"{(value >= 100 || exponent == 0 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.0", CultureInfo.InvariantCulture))} {units[exponent]}";
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
