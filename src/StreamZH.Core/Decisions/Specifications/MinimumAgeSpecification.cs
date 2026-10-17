// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Usenet propagation: a release younger than the minimum age is rejected for now (temporary, so a later RSS sync or
/// search can take it). Skipped without a publish date.
/// </summary>
public sealed class MinimumAgeSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var minimum = context.Rules.MinimumAgeMinutes;
        if (minimum <= 0 || candidate.PublishDate == default)
        {
            return null;
        }

        var age = context.Now - candidate.PublishDate;
        if (age.TotalMinutes >= minimum)
        {
            return null;
        }

        var minutes = Math.Max(0, (int)Math.Floor(age.TotalMinutes));
        return new Rejection(
            RejectionCodes.MinimumAge,
            string.Create(CultureInfo.InvariantCulture, $"Only {minutes} minutes old, the minimum age is {minimum} minutes"),
            RejectionKind.Temporary);
    }
}
