// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>Releases that failed before for this item are not grabbed again (title match, case-insensitive).</summary>
public sealed class BlocklistSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var title = candidate.Release.ReleaseTitle;
        var blocklist = context.Target.BlocklistedTitles;

        var blocked = blocklist.Contains(title)
            || blocklist.Any(t => string.Equals(t, title, StringComparison.OrdinalIgnoreCase));

        return blocked ? new Rejection(RejectionCodes.Blocklisted, "Release is blocklisted") : null;
    }
}
