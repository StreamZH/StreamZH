// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions.Specifications;
using StreamZH.Core.Profiles;

namespace StreamZH.Core.Decisions;

/// <summary>
/// Decides which search results for one movie or one set of episodes may be grabbed and ranks them.
/// Pure logic, modelled on Radarr/Sonarr's DownloadDecisionMaker: every specification runs for every candidate,
/// so a rejected release carries all its reasons.
/// </summary>
public static class DecisionEngine
{
    /// <summary>The built-in rules, in the order their rejections are reported.</summary>
    public static IReadOnlyList<IDecisionSpecification> DefaultSpecifications { get; } =
    [
        new MonitoredSpecification(),
        new AvailabilitySpecification(),
        new QualityAllowedByProfileSpecification(),
        new LanguageSpecification(),
        new AcceptableSizeSpecification(),
        new MaximumSizeSpecification(),
        new MinimumAgeSpecification(),
        new RetentionSpecification(),
        new NotSampleSpecification(),
        new HardcodedSubsSpecification(),
        new ReleaseProfileSpecification(),
        new CustomFormatScoreSpecification(),
        new BlocklistSpecification(),
        new UpgradeSpecification(),
    ];

    /// <summary>Accepted decisions first, ranked best-first; rejected ones after (in the same ranking), each with all reasons.</summary>
    public static IReadOnlyList<Decision> Evaluate(
        IEnumerable<ReleaseCandidate> candidates,
        DecisionTarget target,
        IReadOnlyDictionary<int, QualityDefinition> definitions,
        DateTimeOffset now) =>
        Evaluate(candidates, target, definitions, now, DefaultSpecifications, ReleaseRules.None);

    /// <summary>The built-in rules plus the release rules from Settings → Indexers.</summary>
    public static IReadOnlyList<Decision> Evaluate(
        IEnumerable<ReleaseCandidate> candidates,
        DecisionTarget target,
        IReadOnlyDictionary<int, QualityDefinition> definitions,
        DateTimeOffset now,
        ReleaseRules rules) =>
        Evaluate(candidates, target, definitions, now, DefaultSpecifications, rules);

    /// <summary>Same as the default overload, with a custom set of specifications.</summary>
    public static IReadOnlyList<Decision> Evaluate(
        IEnumerable<ReleaseCandidate> candidates,
        DecisionTarget target,
        IReadOnlyDictionary<int, QualityDefinition> definitions,
        DateTimeOffset now,
        IReadOnlyList<IDecisionSpecification> specifications) =>
        Evaluate(candidates, target, definitions, now, specifications, ReleaseRules.None);

    /// <summary>Custom specifications and release rules.</summary>
    public static IReadOnlyList<Decision> Evaluate(
        IEnumerable<ReleaseCandidate> candidates,
        DecisionTarget target,
        IReadOnlyDictionary<int, QualityDefinition> definitions,
        DateTimeOffset now,
        IReadOnlyList<IDecisionSpecification> specifications,
        ReleaseRules rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(specifications);
        ArgumentNullException.ThrowIfNull(rules);

        var context = new DecisionContext(target, definitions, now) { Rules = rules };
        var comparer = new DecisionComparer(context);

        return candidates
            .Select(candidate =>
            {
                var formats = context.CustomFormatsFor(candidate);
                return new Decision(candidate, Check(candidate, context, specifications))
                {
                    PreferredScore = comparer.PreferredScore(candidate),
                    CustomFormats = formats.Formats,
                    CustomFormatScore = formats.Score,
                };
            })
            .OrderByDescending(d => d.Accepted)
            .ThenByDescending(d => d.Candidate, comparer)
            .ToList();
    }

    /// <summary>The ranked accepted decisions only.</summary>
    public static IReadOnlyList<Decision> Accepted(IReadOnlyList<Decision> decisions) =>
        decisions.Where(d => d.Accepted).ToList();

    private static List<Rejection> Check(
        ReleaseCandidate candidate,
        DecisionContext context,
        IReadOnlyList<IDecisionSpecification> specifications)
    {
        var rejections = new List<Rejection>();

        foreach (var specification in specifications)
        {
            try
            {
                if (specification.Evaluate(candidate, context) is { } rejection)
                {
                    rejections.Add(rejection);
                }
            }
            catch (Exception e)
            {
                // A broken rule must not break the whole search; report it on the release instead (like Radarr).
                rejections.Add(new Rejection(RejectionCodes.DecisionError, $"{specification.GetType().Name}: {e.Message}"));
            }
        }

        return rejections;
    }
}
