// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>Releases larger than the maximum size for all releases (MB, 0 = unlimited) are rejected; unknown sizes pass.</summary>
public sealed class MaximumSizeSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var maximum = context.Rules.MaximumSizeMb;
        if (maximum <= 0 || candidate.SizeBytes <= 0)
        {
            return null;
        }

        var maxBytes = maximum * SizeRules.Megabyte;
        return candidate.SizeBytes > maxBytes
            ? new Rejection(
                RejectionCodes.MaximumSizeExceeded,
                $"{SizeRules.Format(candidate.SizeBytes)} is larger than the maximum size of {SizeRules.Format(maxBytes)} for all releases")
            : null;
    }
}
