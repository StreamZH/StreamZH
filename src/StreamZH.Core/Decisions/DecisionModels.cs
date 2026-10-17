// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

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
    string Guid);

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
    IReadOnlySet<string> BlocklistedTitles);

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
    public QualityProfile Profile => Target.Profile;

    public QualityDefinition? DefinitionFor(int qualityId) =>
        Definitions.TryGetValue(qualityId, out var definition) ? definition : null;
}
