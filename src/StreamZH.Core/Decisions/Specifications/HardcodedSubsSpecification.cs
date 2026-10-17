// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>Releases with hardcoded subtitles are rejected unless they are allowed or the tag is whitelisted (Radarr).</summary>
public sealed class HardcodedSubsSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        if (context.Rules.AllowHardcodedSubs || HardcodedSubs.Find(candidate.Release.ReleaseTitle) is not { } found)
        {
            return null;
        }

        return HardcodedSubs.IsWhitelisted(found, context.Rules.WhitelistedHardcodedSubs)
            ? null
            : new Rejection(RejectionCodes.HardcodedSubs, $"Hardcoded subs found: {found}");
    }
}
