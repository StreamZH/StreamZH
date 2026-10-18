// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Radarr's CustomFormatAllowedByProfileSpecification: a release whose custom format score is below the profile's
/// minimum custom format score is rejected (permanently).
/// </summary>
public sealed class CustomFormatScoreSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var minimum = context.Profile.MinFormatScore;
        var result = context.CustomFormatsFor(candidate);
        if (result.Score >= minimum)
        {
            return null;
        }

        var formats = result.Formats.Count == 0 ? string.Empty : $" ({string.Join(", ", result.Formats.Select(f => f.Name))})";
        return new Rejection(
            RejectionCodes.CustomFormatMinimumScore,
            string.Create(CultureInfo.InvariantCulture, $"Custom format score {result.Score} is below the minimum {minimum}{formats}"));
    }
}
