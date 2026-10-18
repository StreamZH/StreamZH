// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.Notifications;

/// <summary>Turns the app events into <see cref="ConnectEvent"/>s: library data, parsed quality and group, file sizes.</summary>
public sealed class ConnectEventFactory(MovieRepository movies, SeriesRepository series)
{
    public ConnectEvent Grab(ReleaseGrabbed grabbed)
    {
        ArgumentNullException.ThrowIfNull(grabbed);
        var media = Media(grabbed.MediaType, grabbed.MovieId, grabbed.SeriesId);
        var parsed = ReleaseParser.Parse(grabbed.Title);
        return new ConnectEvent
        {
            Type = ConnectEventType.Grab,
            MediaType = grabbed.MediaType,
            Media = media,
            Episodes = grabbed.MediaType == MediaType.Series && grabbed.SeriesId is { } seriesId ? EpisodesOf(seriesId, parsed) : [],
            Release = Release(grabbed.Title, grabbed.Indexer, null),
        };
    }

    public ConnectEvent Imported(MediaImported imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var media = Media(imported.MediaType, imported.MovieId, imported.SeriesId)
            ?? new ConnectMedia(imported.MovieId ?? imported.SeriesId, imported.Title, 0, 0, null, null, null, [], null, null, imported.Folder);
        var files = imported.Files.Select(f => new ConnectFile(f, Relative(imported.Folder, f), SizeOf(f))).ToList();
        var name = imported.ReleaseTitle ?? (files.Count > 0 ? Path.GetFileNameWithoutExtension(files[0].Path) : imported.Title);
        var episodes = imported.MediaType == MediaType.Series && imported.SeriesId is { } seriesId
            ? series.Episodes(seriesId).Where(e => imported.EpisodeIds.Contains(e.Id)).Select(Episode).ToList()
            : [];
        return new ConnectEvent
        {
            Type = imported.IsUpgrade ? ConnectEventType.Upgrade : ConnectEventType.Import,
            MediaType = imported.MediaType,
            Media = media,
            Episodes = episodes,
            Release = Release(name, null, files.Count > 0 ? files.Sum(f => f.Size) : null),
            Files = files,
            Folder = imported.Folder,
            DownloadId = imported.DownloadId,
        };
    }

    public ConnectEvent Renamed(MediaRenamed renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        return new ConnectEvent
        {
            Type = ConnectEventType.Rename,
            MediaType = renamed.MediaType,
            Media = Media(renamed.MediaType, renamed.MovieId, renamed.SeriesId),
            Folder = renamed.Folder,
        };
    }

    public ConnectEvent Failed(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var link = item.Link;
        var parsed = ReleaseParser.Parse(item.Title);
        return new ConnectEvent
        {
            Type = ConnectEventType.DownloadFailure,
            MediaType = link.MediaType,
            Media = Media(link.MediaType, link.MovieId, link.SeriesId),
            Episodes = link.MediaType == MediaType.Series && link.SeriesId is { } seriesId
                ? (link.EpisodeIds.Count > 0
                    ? [.. series.Episodes(seriesId).Where(e => link.EpisodeIds.Contains(e.Id)).Select(Episode)]
                    : EpisodesOf(seriesId, parsed))
                : [],
            Release = Release(item.Title, item.Indexer, item.TotalBytes > 0 ? item.TotalBytes : null),
            DownloadId = item.Id,
            Message = item.Error,
        };
    }

    public static ConnectEvent HealthChanged(ConnectHealth health, bool restored) => new()
    {
        Type = restored ? ConnectEventType.HealthRestored : ConnectEventType.HealthIssue,
        Health = health,
    };

    private ConnectMedia? Media(MediaType? type, int? movieId, int? seriesId)
    {
        if (type == MediaType.Movie && movieId is { } m && movies.Find(m) is { } movie)
        {
            return new ConnectMedia(
                movie.Id, movie.Title, movie.Year, movie.TmdbId, movie.ImdbId, null, movie.Overview, movie.Genres, movie.PosterPath, movie.BackdropPath, movie.FolderPath);
        }

        if (type == MediaType.Series && seriesId is { } s && series.Find(s) is { } show)
        {
            return new ConnectMedia(
                show.Id, show.Title, show.Year, show.TmdbId, show.ImdbId, show.TvdbId, show.Overview, show.Genres, show.PosterPath, show.BackdropPath, show.FolderPath);
        }

        return null;
    }

    /// <summary>The library episodes a release name points to (S01E02, S01E01-E03, a whole season, an air date, absolute numbers).</summary>
    private List<ConnectEpisode> EpisodesOf(int seriesId, Core.Quality.ParsedRelease? parsed)
    {
        if (parsed is not { IsEpisode: true } || series.Find(seriesId) is not { } show)
        {
            return [];
        }

        return [.. Search.ReleaseMatching.MapEpisodes(parsed, series.Episodes(seriesId), show).Episodes
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .Select(Episode)];
    }

    private static ConnectEpisode Episode(Episode e) => new(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirDate, e.Overview);

    private static ConnectRelease Release(string title, string? indexer, long? size)
    {
        var quality = QualityParser.ParseQuality(title);
        return new ConnectRelease(
            title,
            quality.Quality == Core.Quality.Quality.Unknown ? null : quality.ToString(),
            ReleaseGroupParser.ParseReleaseGroup(title),
            indexer,
            size);
    }

    private static string Relative(string folder, string file)
    {
        var relative = Path.GetRelativePath(folder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(file) : relative;
    }

    private static long SizeOf(string file)
    {
        try
        {
            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
