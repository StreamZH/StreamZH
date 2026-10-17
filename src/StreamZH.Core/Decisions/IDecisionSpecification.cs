// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions;

/// <summary>One rule of the decision engine. Must be pure: no I/O, no state.</summary>
public interface IDecisionSpecification
{
    /// <summary>Null when the candidate passes this rule, otherwise the reason it does not.</summary>
    Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context);
}
