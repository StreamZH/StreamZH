// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Wanted;

/// <summary>
/// One day of the calendar: a movie release (in cinemas, digital, physical) or an episode air date.
/// <see cref="Date"/> is a calendar day (yyyy-MM-dd) as TMDB gives it, without a time of day or time zone.
/// </summary>
/// <param name="ReleaseType">"inCinemas", "digital", "physical" (movies) or "airDate" (episodes).</param>
/// <param name="Status">"downloaded", "downloading" (in the queue or waiting for the import), "missing" or "unaired" (not released yet).</param>
public sealed record CalendarEntry(
    string Key,
    string Type,
    string ReleaseType,
    string Date,
    int? MovieId,
    int? SeriesId,
    int? EpisodeId,
    string Title,
    int Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? Overview,
    string? Network,
    bool Monitored,
    bool HasFile,
    string Status,
    string? Queue,
    string? PosterPath);

/// <param name="Start">First day (inclusive).</param>
/// <param name="End">Last day (inclusive).</param>
public sealed record CalendarQuery(DateOnly Start, DateOnly End, bool IncludeUnmonitored = false, bool Movies = true, bool Series = true)
{
    /// <summary>Longest range one request may ask for (a bit more than a year, for the agenda and the iCal feed).</summary>
    public const int MaxDays = 400;
}

public static class CalendarStatus
{
    public const string Downloaded = "downloaded";
    public const string Downloading = "downloading";
    public const string Missing = "missing";
    public const string Unaired = "unaired";
}

/// <summary>Calendar of Radarr and Sonarr in one: movie release dates and episode air dates in a date range.</summary>
public sealed class CalendarService(
    MovieRepository movies,
    SeriesRepository series,
    WantedQueries queries,
    QueueSnapshotFactory queue,
    IndexerOptions? options = null,
    TimeProvider? timeProvider = null)
{
    public const string InCinemas = "inCinemas";
    public const string Digital = "digital";
    public const string Physical = "physical";
    public const string AirDate = "airDate";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<CalendarEntry> Entries(CalendarQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var from = Day(query.Start);
        var to = Day(query.End);
        if (string.CompareOrdinal(from, to) > 0)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var snapshot = queue.Take();
        var entries = new List<CalendarEntry>();

        if (query.Movies)
        {
            foreach (var movie in movies.All())
            {
                if (!movie.Monitored && !query.IncludeUnmonitored)
                {
                    continue;
                }

                var status = MovieStatus(movie, snapshot, now);
                foreach (var (type, date) in new[] { (InCinemas, movie.InCinemas), (Digital, movie.DigitalRelease), (Physical, movie.PhysicalRelease) })
                {
                    if (date is { Length: 10 } && string.CompareOrdinal(date, from) >= 0 && string.CompareOrdinal(date, to) <= 0)
                    {
                        entries.Add(new CalendarEntry(
                            $"movie-{movie.Id}-{type}", "movie", type, date, movie.Id, null, null, movie.Title, movie.Year, null, null, null,
                            movie.Overview, null, movie.Monitored, movie.File is not null, status, snapshot.Movie(movie.Id), movie.PosterPath));
                    }
                }
            }
        }

        if (query.Series)
        {
            var shows = series.All().ToDictionary(s => s.Id);
            foreach (var episode in queries.EpisodesAiring(from, to))
            {
                if (!shows.TryGetValue(episode.SeriesId, out var show))
                {
                    continue;
                }

                var monitored = show.Monitored && episode.Monitored;
                if (!monitored && !query.IncludeUnmonitored)
                {
                    continue;
                }

                var inQueue = snapshot.Episode(episode.Id);
                var status = episode.File is not null ? CalendarStatus.Downloaded
                    : inQueue is not null ? CalendarStatus.Downloading
                    : string.CompareOrdinal(episode.AirDate, today) <= 0 ? CalendarStatus.Missing
                    : CalendarStatus.Unaired;
                entries.Add(new CalendarEntry(
                    $"episode-{episode.Id}", "episode", AirDate, episode.AirDate!, null, show.Id, episode.Id, show.Title, show.Year,
                    episode.SeasonNumber, episode.EpisodeNumber, episode.Title, episode.Overview, show.Network, monitored, episode.File is not null,
                    status, inQueue, show.PosterPath));
            }
        }

        return [.. entries
            .OrderBy(e => e.Date, StringComparer.Ordinal)
            .ThenBy(e => e.Type == "movie" ? 0 : 1)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SeasonNumber ?? 0)
            .ThenBy(e => e.EpisodeNumber ?? 0)];
    }

    /// <summary>Like Radarr: downloaded, in the queue, missing once available (minimum availability), else not released yet.</summary>
    private string MovieStatus(Movie movie, QueueSnapshot snapshot, DateTimeOffset now)
    {
        if (movie.File is not null)
        {
            return CalendarStatus.Downloaded;
        }

        if (snapshot.Movie(movie.Id) is not null)
        {
            return CalendarStatus.Downloading;
        }

        var available = options?.IsAvailable(movie, now)
            ?? IndexerOptions.AvailableFrom(movie, IndexerOptions.Defaults.MinimumAvailability, 0, now) is null;
        return available ? CalendarStatus.Missing : CalendarStatus.Unaired;
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
