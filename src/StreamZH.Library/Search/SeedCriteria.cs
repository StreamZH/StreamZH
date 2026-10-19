// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;

namespace StreamZH.Library.Search;

/// <summary>Seed goals handed to the torrent client with a grab (null = the client's own default).</summary>
public sealed record SeedGoal(double? Ratio, int? TimeMinutes)
{
    public static readonly SeedGoal None = new(null, null);
}

/// <summary>
/// Radarr/Sonarr's SeedConfigProvider: the indexer's seed ratio and seed time (Sonarr: the season pack seed time for full
/// seasons). StreamZH also respects the tracker's own minimums when the feed names them (Torznab minimumratio and
/// minimumseedtime, as Prowlarr sends them), so a lower setting cannot cause a hit-and-run.
/// </summary>
public static class SeedCriteria
{
    public static SeedGoal For(IndexerDefinition indexer, ReleaseDecision decision)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(decision);
        if (indexer.Protocol != DownloadProtocol.Torrent)
        {
            return SeedGoal.None;
        }

        var release = decision.Release;
        var seasonPack = decision.Parsed is { IsEpisode: true, FullSeason: true };
        var time = seasonPack ? indexer.SeasonPackSeedTimeMinutes ?? indexer.SeedTimeMinutes : indexer.SeedTimeMinutes;
        int? trackerMinutes = release.MinimumSeedTimeSeconds is > 0 and var seconds ? (int)Math.Min(int.MaxValue, (seconds + 59) / 60) : null;

        return new SeedGoal(Max(indexer.SeedRatio, release.MinimumRatio), Max(time, trackerMinutes));
    }

    private static T? Max<T>(T? a, T? b)
        where T : struct, IComparable<T> =>
        a is { } x && b is { } y ? (x.CompareTo(y) >= 0 ? x : y) : a ?? b;
}
