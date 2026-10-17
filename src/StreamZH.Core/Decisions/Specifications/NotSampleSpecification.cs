// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Rejects sample releases (title contains "sample" and smaller than 70 MB, like Radarr). Catches samples even
/// when the runtime is unknown and the size check is skipped.
/// </summary>
public sealed class NotSampleSpecification : IDecisionSpecification
{
    public const long MaxSampleSize = 70 * SizeRules.Megabyte;

    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var isSample = candidate.Release.ReleaseTitle.Contains("sample", StringComparison.OrdinalIgnoreCase)
            && candidate.SizeBytes > 0
            && candidate.SizeBytes < MaxSampleSize;

        return isSample ? new Rejection(RejectionCodes.Sample, "Release is a sample") : null;
    }
}
