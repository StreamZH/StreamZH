// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Decisions;

/// <summary>
/// Ranks candidates for one target, modelled on Radarr's DownloadDecisionComparer.
/// Compare(x, y) &gt; 0 means x is better. Rules, first difference wins:
/// <list type="number">
/// <item>Profile quality rank, higher first.</item>
/// <item>Revision (REAL, then version), higher first; skipped with <see cref="ProperHandling.DoNotPrefer"/>.</item>
/// <item>Dual language first, when the profile prefers it.</item>
/// <item>Preferred score of the release profiles, higher first (after quality and language, like Sonarr v3).</item>
/// <item>Indexer priority, lower number first.</item>
/// <item>Size: closest to the preferred size (per minute × runtime) when one is set, otherwise larger first;
/// rounded to 200 MB steps like Radarr so tiny differences fall through.</item>
/// <item>Publish date, newer first.</item>
/// </list>
/// </summary>
public sealed class DecisionComparer : IComparer<ReleaseCandidate>
{
    private const long SizeStep = 200 * SizeRules.Megabyte;

    private readonly DecisionContext _context;
    private readonly Dictionary<ReleaseCandidate, int> _scores = new(ReferenceEqualityComparer.Instance);

    public DecisionComparer(DecisionContext context)
    {
        _context = context;
    }

    /// <summary>Preferred score of the release profiles for this target, computed once per candidate.</summary>
    public int PreferredScore(ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_context.Rules.ReleaseProfiles.Count == 0)
        {
            return 0;
        }

        lock (_scores)
        {
            if (!_scores.TryGetValue(candidate, out var score))
            {
                score = _context.PreferredScore(candidate);
                _scores[candidate] = score;
            }

            return score;
        }
    }

    public int Compare(ReleaseCandidate? x, ReleaseCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int[] results =
        [
            CompareQuality(x, y),
            _context.Target.Propers == ProperHandling.DoNotPrefer ? 0 : x.Release.Quality.Revision.CompareTo(y.Release.Quality.Revision),
            CompareDualLanguage(x, y),
            PreferredScore(x).CompareTo(PreferredScore(y)),
            y.IndexerPriority.CompareTo(x.IndexerPriority),
            SizeScore(x).CompareTo(SizeScore(y)),
            x.PublishDate.CompareTo(y.PublishDate),
        ];

        return results.FirstOrDefault(r => r != 0);
    }

    private int CompareQuality(ReleaseCandidate x, ReleaseCandidate y) =>
        _context.Profile.RankOf(x.Release.Quality.Quality.Id).CompareTo(_context.Profile.RankOf(y.Release.Quality.Quality.Id));

    private int CompareDualLanguage(ReleaseCandidate x, ReleaseCandidate y)
    {
        if (!_context.Profile.PreferDualLanguage)
        {
            return 0;
        }

        return IsDual(x).CompareTo(IsDual(y));
    }

    private bool IsDual(ReleaseCandidate candidate) =>
        LanguageRules.IsDualLanguage(
            LanguageRules.EffectiveLanguages(candidate.Release, _context.Target.OriginalLanguage),
            _context.Profile,
            _context.Target.OriginalLanguage);

    /// <summary>Higher is better.</summary>
    private long SizeScore(ReleaseCandidate candidate)
    {
        var minutes = SizeRules.TotalRuntimeMinutes(_context.Target);
        var preferred = _context.DefinitionFor(candidate.Release.Quality.Quality.Id)?.PreferredSize;

        if (preferred is > 0 && minutes > 0)
        {
            var preferredBytes = SizeRules.ToBytes(preferred.Value, minutes);
            return -Math.Abs(RoundToStep(candidate.SizeBytes - preferredBytes));
        }

        return RoundToStep(candidate.SizeBytes);
    }

    private static long RoundToStep(long bytes) => (long)Math.Round(bytes / (double)SizeStep) * SizeStep;
}
