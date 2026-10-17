// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) Sonarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// Sonarr's release profile restrictions: a release must contain at least one "must contain" term and none of the
/// "must not contain" terms of every enabled profile that applies to its indexer and quality profile.
/// All failing profiles are named in one rejection.
/// </summary>
public sealed class ReleaseProfileSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        if (context.Rules.ReleaseProfiles.Count == 0)
        {
            return null;
        }

        var failing = ReleaseProfileRules
            .Explain(context.Rules, candidate.IndexerId, context.Profile.Id, candidate.Release.ReleaseTitle)
            .Where(m => m.Rejects)
            .ToList();
        if (failing.Count == 0)
        {
            return null;
        }

        var reasons = new List<string>();
        foreach (var match in failing)
        {
            if (match.IgnoredMatched.Count > 0)
            {
                reasons.Add($"Release profile '{match.Profile.Name}': contains {Quote(match.IgnoredMatched, "and")}");
            }

            if (match.MissingRequired)
            {
                reasons.Add($"Release profile '{match.Profile.Name}': does not contain {Quote(match.Profile.Required, "or")}");
            }
        }

        var code = failing.Any(m => m.IgnoredMatched.Count > 0) ? RejectionCodes.MustNotContainPresent : RejectionCodes.MustContainMissing;
        return new Rejection(code, string.Join("; ", reasons));
    }

    private static string Quote(IReadOnlyList<string> terms, string joiner) => terms.Count == 1
        ? $"'{terms[0]}'"
        : $"{string.Join(", ", terms.Take(terms.Count - 1).Select(t => $"'{t}'"))} {joiner} '{terms[^1]}'";
}
