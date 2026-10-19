// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Radarr's DelaySpecification with one global delay profile (Settings → Indexers): a release younger than its protocol's
/// delay is rejected for now (temporary), so a better release can turn up in the meantime; automatic flows keep it as a
/// pending release and grab the best one once the delay has passed. Not delayed: delay 0, a release of the preferred
/// protocol in the profile's best allowed quality (when "bypass" is on), a better revision of the file on disk of the
/// preferred protocol, and everything once the oldest pending release of the item has waited the full delay.
/// Searches started by the user run without delays (the caller removes them from the rules).
/// </summary>
public sealed class DelaySpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var rules = context.Rules;
        var delay = rules.DelayFor(candidate.Protocol);
        if (delay <= 0 || candidate.PublishDate == default)
        {
            return null;
        }

        var preferred = rules.IsPreferred(candidate.Protocol);
        var profile = context.Profile;
        var quality = candidate.Release.Quality;

        if (preferred && context.Target.ExistingQuality is { } existing
            && existing.Quality.Id == quality.Quality.Id && quality.Revision.CompareTo(existing.Revision) > 0)
        {
            return null;
        }

        if (preferred && rules.BypassDelayIfHighestQuality && IsBestInProfile(profile, quality.Quality.Id))
        {
            return null;
        }

        var limit = TimeSpan.FromMinutes(delay);
        if (context.Target.PendingSince is { } since && context.Now - since > limit)
        {
            return null;
        }

        var age = context.Now - candidate.PublishDate;
        if (age >= limit)
        {
            return null;
        }

        var left = Math.Max(1, (int)Math.Ceiling((limit - age).TotalMinutes));
        var protocol = candidate.Protocol == Downloads.DownloadProtocol.Torrent ? "torrents" : "Usenet";
        return new Rejection(
            RejectionCodes.MinimumAgeDelay,
            string.Create(CultureInfo.InvariantCulture, $"Waiting for a better release: {delay} minute delay for {protocol}, {left} minutes left"),
            RejectionKind.Temporary);
    }

    /// <summary>Radarr's LastAllowedQuality: the quality is at least as good as the best allowed one of the profile.</summary>
    private static bool IsBestInProfile(Profiles.QualityProfile profile, int qualityId)
    {
        var rank = profile.RankOf(qualityId);
        if (rank < 0)
        {
            return false;
        }

        var best = profile.Items.Where(i => i.Allowed).Select(i => profile.RankOf(i.QualityId)).DefaultIfEmpty(-1).Max();
        return best >= 0 && rank >= best;
    }
}
