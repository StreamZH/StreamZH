// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>The release quality must be known and allowed in the profile.</summary>
public sealed class QualityAllowedByProfileSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var quality = candidate.Release.Quality.Quality;

        if (quality == Quality.Quality.Unknown)
        {
            return new Rejection(RejectionCodes.UnknownQuality, "Quality could not be determined");
        }

        if (!context.Profile.IsAllowed(quality.Id))
        {
            return new Rejection(RejectionCodes.QualityNotWanted, $"Quality {quality.Name} is not wanted in profile {context.Profile.Name}");
        }

        return null;
    }
}
