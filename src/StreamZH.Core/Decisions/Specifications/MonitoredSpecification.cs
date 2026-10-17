// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>Unmonitored movies and episodes are never grabbed automatically.</summary>
public sealed class MonitoredSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        if (context.Target.Monitored)
        {
            return null;
        }

        var what = context.Target.MediaType switch
        {
            MediaType.Movie => "Movie",
            MediaType.Series => "Episode",
            _ => "Item",
        };

        return new Rejection(RejectionCodes.NotMonitored, $"{what} is not monitored");
    }
}
