// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Radarr's minimum availability: a movie that is not available yet is not grabbed by automatic searches and RSS.
/// Temporary; the target carries the date (null = available, see <see cref="DecisionTarget.AvailableFrom"/>).
/// </summary>
public sealed class AvailabilitySpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        if (context.Target.AvailableFrom is not { } from || context.Now >= from)
        {
            return null;
        }

        var when = from == DateTimeOffset.MaxValue
            ? "not available yet"
            : string.Create(CultureInfo.InvariantCulture, $"only available from {from:yyyy-MM-dd}");
        return new Rejection(RejectionCodes.Availability, $"Movie is {when} (minimum availability)", RejectionKind.Temporary);
    }
}
