// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Core.Decisions;

/// <summary>One search result from an indexer, already parsed.</summary>
/// <param name="IndexerPriority">Lower number = higher priority (1 is best, like Radarr's 1-50 range).</param>
public sealed record ReleaseCandidate(
    ParsedRelease Release,
    long SizeBytes,
    string Indexer,
    int IndexerPriority,
    DateTimeOffset PublishDate,
    string DownloadUrl,
    string Guid)
{
    /// <summary>Id of the indexer (0 when unknown, e.g. the release test page); release profiles can be limited to indexers.</summary>
    public int IndexerId { get; init; }

    /// <summary>Indexer flags (freeleech, internal …) as bit flags of the media type's enum; Usenet indexers set none yet (0).</summary>
    public int IndexerFlags { get; init; }
}

/// <summary>What the search was for: one movie, or one set of episodes (single episode, multi-episode or season pack).</summary>
/// <param name="RuntimeMinutes">Runtime of the movie or of one episode; 0 when unknown (size check is skipped).</param>
/// <param name="EpisodeCount">Series only: number of episodes the release covers (for a season pack, the episodes in the pack).</param>
/// <param name="ExistingQuality">Quality of the file already on disk, or null when there is none.</param>
/// <param name="ExistingLanguages">Audio languages of the file on disk (empty when unknown or no file).</param>
/// <param name="BlocklistedTitles">Release titles that failed before for this item; compared case-insensitively.</param>
public sealed record DecisionTarget(
    MediaType MediaType,
    string Title,
    int Year,
    int RuntimeMinutes,
    int EpisodeCount,
    QualityProfile Profile,
    Language OriginalLanguage,
    QualityModel? ExistingQuality,
    IReadOnlyList<Language> ExistingLanguages,
    bool Monitored,
    IReadOnlySet<string> BlocklistedTitles)
{
    /// <summary>
    /// Movies: from when the movie counts as available (minimum availability, Settings → Indexers); null = available.
    /// Only automatic searches and RSS set it; an interactive search ignores availability like Radarr.
    /// </summary>
    public DateTimeOffset? AvailableFrom { get; init; }

    /// <summary>File name of the file on disk (for the preferred score of release profiles); null when there is none.</summary>
    public string? ExistingFileName { get; init; }

    /// <summary>Settings → Media Management → "Propers and repacks"; the default keeps the old behaviour.</summary>
    public ProperHandling Propers { get; init; } = ProperHandling.PreferAndUpgrade;

    /// <summary>The custom formats of this media type; releases are scored against them with the profile's scores.</summary>
    public IReadOnlyList<CustomFormat> CustomFormats { get; init; } = [];

    /// <summary>The custom formats the file on disk matches (from its name, release group and quality); empty without a file.</summary>
    public IReadOnlyList<CustomFormat> ExistingCustomFormats { get; init; } = [];
}

/// <summary>How PROPER, REPACK and REAL releases (a higher revision of the same quality) are treated, like Radarr and Sonarr.</summary>
public enum ProperHandling
{
    /// <summary>A higher revision ranks first and replaces the file on disk (default).</summary>
    PreferAndUpgrade,

    /// <summary>A higher revision ranks first, but a file on disk is not replaced just for a newer revision.</summary>
    DoNotUpgrade,

    /// <summary>Revisions are ignored: they neither rank first nor replace files (use release profiles to rank them).</summary>
    DoNotPrefer,
}

/// <summary>
/// Permanent: this release will never be wanted for the target as it is now.
/// Temporary: it might be wanted later (e.g. once a delay or minimum age has passed).
/// </summary>
public enum RejectionKind
{
    Permanent,
    Temporary,
}

/// <summary>Why a release was not accepted. <paramref name="Code"/> is stable (see <see cref="RejectionCodes"/>), <paramref name="Message"/> is for the UI.</summary>
public sealed record Rejection(string Code, string Message, RejectionKind Kind = RejectionKind.Permanent)
{
    public override string ToString() => Message;
}

/// <summary>The verdict for one candidate: accepted when there are no rejections.</summary>
public sealed record Decision(ReleaseCandidate Candidate, IReadOnlyList<Rejection> Rejections)
{
    /// <summary>Sum of the matching preferred terms of the release profiles (0 without release profiles).</summary>
    public int PreferredScore { get; init; }

    /// <summary>The custom formats the release matches, ordered by name.</summary>
    public IReadOnlyList<CustomFormat> CustomFormats { get; init; } = [];

    /// <summary>Their summed score in the target's quality profile.</summary>
    public int CustomFormatScore { get; init; }

    public bool Accepted => Rejections.Count == 0;

    /// <summary>Rejected, but only for reasons that may go away later.</summary>
    public bool TemporarilyRejected => Rejections.Count > 0 && Rejections.All(r => r.Kind == RejectionKind.Temporary);

    public bool PermanentlyRejected => Rejections.Any(r => r.Kind == RejectionKind.Permanent);
}

/// <summary>Everything a specification may look at besides the candidate itself.</summary>
public sealed record DecisionContext(
    DecisionTarget Target,
    IReadOnlyDictionary<int, QualityDefinition> Definitions,
    DateTimeOffset Now)
{
    /// <summary>Release rules from Settings → Indexers; <see cref="ReleaseRules.None"/> when not given.</summary>
    public ReleaseRules Rules { get; init; } = ReleaseRules.None;

    public QualityProfile Profile => Target.Profile;

    /// <summary>Preferred score of a release name for this target's quality profile (see <see cref="ReleaseProfileRules"/>).</summary>
    public int PreferredScore(ReleaseCandidate candidate) =>
        ReleaseProfileRules.Score(Rules, candidate.IndexerId, Profile.Id, candidate.Release.ReleaseTitle);

    public QualityDefinition? DefinitionFor(int qualityId) =>
        Definitions.TryGetValue(qualityId, out var definition) ? definition : null;

    private readonly FormatCache _formats = new();

    /// <summary>
    /// The custom formats a release matches and their score in the profile, computed once per candidate and target
    /// (the engine's specifications and the ranking share it).
    /// </summary>
    public CustomFormatResult CustomFormatsFor(ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Target.CustomFormats.Count == 0)
        {
            return CustomFormatResult.None;
        }

        return _formats.Get(Target, candidate, () =>
        {
            var input = CustomFormatInput.ForRelease(
                candidate.Release, candidate.SizeBytes, Target.MediaType, Target.OriginalLanguage, Target.Year, candidate.IndexerFlags);
            return CustomFormatCalculator.Calculate(Profile, Target.CustomFormats, input);
        });
    }

    /// <summary>Score of the file on disk in the profile (0 without a file or formats).</summary>
    public int ExistingCustomFormatScore => CustomFormatCalculator.Score(Profile, Target.ExistingCustomFormats);

    /// <summary>Per-candidate cache; a context copied with another target never reuses results of the old one.</summary>
    private sealed class FormatCache
    {
        private readonly Dictionary<ReleaseCandidate, (DecisionTarget Target, CustomFormatResult Result)> _items = new(ReferenceEqualityComparer.Instance);

        public CustomFormatResult Get(DecisionTarget target, ReleaseCandidate candidate, Func<CustomFormatResult> compute)
        {
            lock (_items)
            {
                if (_items.TryGetValue(candidate, out var entry) && ReferenceEquals(entry.Target, target))
                {
                    return entry.Result;
                }
            }

            var result = compute();
            lock (_items)
            {
                _items[candidate] = (target, result);
            }

            return result;
        }
    }
}
