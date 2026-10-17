// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Size limits from the quality definition (MB per minute) × runtime; for series × the number of episodes in the
/// release. Skipped when the size is unknown (0, like Radarr), the runtime is unknown, the release is a special or
/// the quality has no definition. A max size of null or 0 means unlimited.
/// </summary>
public sealed class AcceptableSizeSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var size = candidate.SizeBytes;
        var minutes = SizeRules.TotalRuntimeMinutes(context.Target);

        if (size <= 0 || minutes <= 0 || candidate.Release.IsSpecial)
        {
            return null;
        }

        var definition = context.DefinitionFor(candidate.Release.Quality.Quality.Id);

        if (definition is null)
        {
            return null;
        }

        if (definition.MinSize > 0)
        {
            var minSize = SizeRules.ToBytes(definition.MinSize, minutes);

            if (size < minSize)
            {
                return new Rejection(
                    RejectionCodes.BelowMinimumSize,
                    $"{SizeRules.Format(size)} is smaller than the minimum of {SizeRules.Format(minSize)} (for {SizeRules.DescribeRuntime(context.Target, minutes)})");
            }
        }

        if (definition.MaxSize is > 0)
        {
            var maxSize = SizeRules.ToBytes(definition.MaxSize.Value, minutes);

            if (size > maxSize)
            {
                return new Rejection(
                    RejectionCodes.AboveMaximumSize,
                    $"{SizeRules.Format(size)} is larger than the maximum of {SizeRules.Format(maxSize)} (for {SizeRules.DescribeRuntime(context.Target, minutes)})");
            }
        }

        return null;
    }
}
