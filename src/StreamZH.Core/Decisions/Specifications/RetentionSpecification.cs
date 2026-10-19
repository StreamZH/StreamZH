// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>Releases older than the retention (days, 0 = unlimited) are rejected: the Usenet servers no longer have them.</summary>
public sealed class RetentionSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        // Retention is the Usenet providers' limit; torrents live as long as someone seeds them (Radarr checks Usenet only).
        var retention = context.Rules.RetentionDays;
        if (retention <= 0 || candidate.PublishDate == default || candidate.Protocol != Downloads.DownloadProtocol.Usenet)
        {
            return null;
        }

        var days = (context.Now - candidate.PublishDate).TotalDays;
        return days > retention
            ? new Rejection(
                RejectionCodes.MaximumAge,
                string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(days):0} days old, older than the retention of {retention} days"))
            : null;
    }
}
