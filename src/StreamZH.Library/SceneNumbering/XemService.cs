// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) its contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Parsing.Releases;

namespace StreamZH.Library.SceneNumbering;

/// <summary>What one update of a series from TheXEM did.</summary>
/// <param name="Mapped">Episodes that have scene numbers now (null: the numbering was left as it was).</param>
/// <param name="Names">Scene names stored as alternative titles (null: left as they were).</param>
public sealed record XemUpdate(int? Mapped, int? Names);

/// <summary>
/// Scene numbering and scene names from TheXEM, like Sonarr's XemService and SceneMappingService. TheXEM maps TheTVDB's
/// numbering to the scene's; StreamZH numbers episodes like TMDB, so a TheTVDB episode is found by season and episode when
/// TMDB has the same seasons, else by its absolute number (seasons split differently). The list of mapped series and the
/// scene names are fetched once and kept for 12 hours. When TheXEM cannot be reached, everything stays as it was.
/// </summary>
public sealed class XemService(
    XemClient client, SeriesRepository series, AlternativeTitleRepository titles, ILogger<XemService> logger, TimeProvider? timeProvider = null)
{
    /// <summary>How long the list of mapped series and the scene names are kept (Sonarr refreshes them every 12 hours too).</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    /// <summary>After a failed request, refreshes wait this long before asking TheXEM again (the scheduled task does not wait).</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, IReadOnlySet<int> Ids)? _mapped;
    private (DateTimeOffset At, IReadOnlyDictionary<int, IReadOnlyList<XemName>> Names)? _names;
    private DateTimeOffset? _failedAt;

    /// <summary>The last problem with TheXEM (null after a good update), for logs and tests.</summary>
    public string? LastError { get; private set; }

    /// <summary>Updates one series (after a refresh). Never throws for TheXEM problems.</summary>
    public async Task<XemUpdate> UpdateSeriesAsync(Series item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.TvdbId is not > 0 || item.Id <= 0)
        {
            return new XemUpdate(null, null);
        }

        var mapped = await MappedSeriesAsync(force: false, cancellationToken);
        var names = await NamesAsync(force: false, cancellationToken);
        return await ApplyAsync(item, mapped, names, cancellationToken);
    }

    /// <summary>The scheduled "Scene mappings" task: fresh lists from TheXEM, then every series with a TVDB id.</summary>
    public async Task<int> UpdateAllAsync(CancellationToken cancellationToken = default)
    {
        var mapped = await MappedSeriesAsync(force: true, cancellationToken);
        var names = await NamesAsync(force: true, cancellationToken);
        if (mapped is null && names is null)
        {
            throw new XemException(LastError ?? "TheXEM is not reachable.");
        }

        var updated = 0;
        foreach (var show in series.All().Where(s => s.TvdbId is > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ApplyAsync(show, mapped, names, cancellationToken);
            updated += result.Mapped > 0 ? 1 : 0;
        }

        logger.LogInformation("Scene mappings updated: {Count} series use scene numbering", updated);
        return updated;
    }

    private async Task<XemUpdate> ApplyAsync(
        Series item, IReadOnlySet<int>? mapped, IReadOnlyDictionary<int, IReadOnlyList<XemName>>? names, CancellationToken cancellationToken)
    {
        var tvdbId = item.TvdbId!.Value;
        int? mappedCount = null;
        if (mapped is not null)
        {
            if (mapped.Contains(tvdbId))
            {
                try
                {
                    var mappings = await client.GetMappingsAsync(tvdbId, cancellationToken);
                    var numbering = Translate(series.Episodes(item.Id), mappings);
                    series.SetSceneNumbering(item.Id, numbering);
                    mappedCount = numbering.Count;
                    logger.LogDebug("TheXEM: {Count} episodes of '{Title}' have scene numbers", numbering.Count, item.Title);
                }
                catch (XemException ex)
                {
                    Fail(ex.Message);
                }
            }
            else if (EpisodeNumbering.HasSceneNumbering(series.Episodes(item.Id)))
            {
                series.SetSceneNumbering(item.Id, new Dictionary<int, SceneNumber>());
                mappedCount = 0;
            }
            else
            {
                mappedCount = 0;
            }
        }

        int? nameCount = null;
        if (names is not null)
        {
            var own = new HashSet<string>([TitleNormalizer.Clean(item.Title), TitleNormalizer.Clean(item.OriginalTitle)], StringComparer.Ordinal);
            var sceneTitles = (names.GetValueOrDefault(tvdbId) ?? [])
                .Select(n => (n, Clean: TitleNormalizer.Clean(n.Title)))
                .Where(x => x.Clean.Length > 0 && (x.n.SeasonNumber is not null || !own.Contains(x.Clean)))
                .DistinctBy(x => (x.Clean, x.n.SeasonNumber))
                .Select(x => new AlternativeTitle
                {
                    MediaType = MediaType.Series,
                    ItemId = item.Id,
                    Title = x.n.Title,
                    CleanTitle = x.Clean,
                    Source = AlternativeTitleSources.Xem,
                    SceneSeasonNumber = x.n.SeasonNumber,
                })
                .ToList();
            titles.ReplaceSource(MediaType.Series, item.Id, AlternativeTitleSources.Xem, sceneTitles);
            nameCount = sceneTitles.Count;
        }

        return new XemUpdate(mappedCount, nameCount);
    }

    /// <summary>
    /// TheXEM maps TheTVDB numbers; this finds the TMDB episode for each mapping and continues the numbering after the last
    /// mapped episode (Sonarr's ExtrapolateMappings), so new episodes of a re-numbered season are found before TheXEM has them.
    /// </summary>
    public static Dictionary<int, SceneNumber> Translate(IReadOnlyList<Episode> episodes, IReadOnlyList<XemMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(mappings);
        var bySeasonEpisode = episodes.GroupBy(e => (e.SeasonNumber, e.EpisodeNumber)).ToDictionary(g => g.Key, g => g.First());
        var byAbsolute = episodes.Where(e => e.AbsoluteEpisodeNumber is not null)
            .GroupBy(e => e.AbsoluteEpisodeNumber!.Value).ToDictionary(g => g.Key, g => g.First());

        // TMDB splits the seasons differently when TheTVDB numbers are missing on TMDB: then the absolute number decides.
        var seasonsDiffer = mappings.Any(m => m.Tvdb.Season > 0 && !bySeasonEpisode.ContainsKey((m.Tvdb.Season, m.Tvdb.Episode)));

        var result = new Dictionary<int, SceneNumber>();
        var mappedEpisodes = new List<(Episode Episode, XemMapping Mapping)>();
        foreach (var mapping in mappings)
        {
            var bySe = bySeasonEpisode.GetValueOrDefault((mapping.Tvdb.Season, mapping.Tvdb.Episode));
            var byAbs = mapping.Tvdb.Season > 0 && mapping.Tvdb.Absolute is { } absolute ? byAbsolute.GetValueOrDefault(absolute) : null;
            var episode = seasonsDiffer ? byAbs ?? bySe : bySe ?? byAbs;
            if (episode is null || result.ContainsKey(episode.Id))
            {
                continue;
            }

            result[episode.Id] = new SceneNumber(mapping.Scene.Season, mapping.Scene.Episode, mapping.Scene.Absolute);
            mappedEpisodes.Add((episode, mapping));
        }

        Extrapolate(episodes, mappedEpisodes, result);
        return result;
    }

    /// <summary>
    /// Episodes after the last mapped one of the last mapped season continue its scene numbering; later seasons move by the
    /// same season offset (Sonarr's ExtrapolateMappings).
    /// </summary>
    private static void Extrapolate(IReadOnlyList<Episode> episodes, List<(Episode Episode, XemMapping Mapping)> mapped, Dictionary<int, SceneNumber> result)
    {
        var regular = mapped.Where(m => m.Episode.SeasonNumber > 0 && m.Mapping.Scene.Season > 0).ToList();
        if (regular.Count == 0)
        {
            return;
        }

        var lastSeason = regular.Max(m => m.Episode.SeasonNumber);
        var lastEpisode = regular.Where(m => m.Episode.SeasonNumber == lastSeason).MaxBy(m => m.Episode.EpisodeNumber).Episode;
        var lastScene = result[lastEpisode.Id];
        var lastSceneSeason = regular.Max(m => m.Mapping.Scene.Season);

        foreach (var episode in episodes.Where(e => e.SeasonNumber > 0 && !result.ContainsKey(e.Id)).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber))
        {
            if (episode.SeasonNumber == lastSeason && episode.EpisodeNumber > lastEpisode.EpisodeNumber)
            {
                var offset = episode.EpisodeNumber - lastEpisode.EpisodeNumber;
                result[episode.Id] = new SceneNumber(
                    lastScene.SeasonNumber, lastScene.EpisodeNumber + offset, lastScene.AbsoluteEpisodeNumber is { } a ? a + offset : null);
            }
            else if (episode.SeasonNumber > lastSeason && lastSceneSeason != lastSeason)
            {
                result[episode.Id] = new SceneNumber(lastSceneSeason + (episode.SeasonNumber - lastSeason), episode.EpisodeNumber, null);
            }
        }
    }

    private async Task<IReadOnlySet<int>?> MappedSeriesAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _mapped is { } cached && _time.GetUtcNow() - cached.At < CacheLifetime)
            {
                return cached.Ids;
            }

            if (!force && RecentlyFailed)
            {
                return _mapped?.Ids;
            }

            var ids = await client.GetSeriesWithMappingsAsync(cancellationToken);
            _mapped = (_time.GetUtcNow(), ids);
            LastError = null;
            return ids;
        }
        catch (XemException ex)
        {
            Fail(ex.Message);

            // A stale list is better than none while TheXEM is down.
            return _mapped?.Ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<XemName>>?> NamesAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _names is { } cached && _time.GetUtcNow() - cached.At < CacheLifetime)
            {
                return cached.Names;
            }

            if (!force && RecentlyFailed)
            {
                return _names?.Names;
            }

            var names = await client.GetSceneNamesAsync(cancellationToken);
            _names = (_time.GetUtcNow(), names);
            return names;
        }
        catch (XemException ex)
        {
            Fail(ex.Message);
            return _names?.Names;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool RecentlyFailed => _failedAt is { } failed && _time.GetUtcNow() - failed < RetryAfterFailure;

    private void Fail(string message)
    {
        LastError = message;
        _failedAt = _time.GetUtcNow();
        logger.LogWarning("TheXEM: {Message} Scene numbering and scene names stay as they were.", message);
    }
}
