// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Downloads;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Radarr's TorrentSeedingSpecification: a torrent with fewer seeders than the indexer's "Minimum seeders" is rejected.
/// Unknown seeders (the indexer does not report them) pass.
/// </summary>
public sealed class TorrentSeedingSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        if (candidate.Protocol != DownloadProtocol.Torrent || candidate.Seeders is not { } seeders || seeders >= candidate.MinimumSeeders)
        {
            return null;
        }

        return new Rejection(
            RejectionCodes.MinimumSeeders,
            string.Create(CultureInfo.InvariantCulture, $"Not enough seeders: {seeders}. Minimum seeders: {candidate.MinimumSeeders}"));
    }
}
