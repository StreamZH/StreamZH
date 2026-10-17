// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;

namespace StreamZH.Tests.Decisions;

/// <summary>Hand-built releases, profiles and targets (no parser involved).</summary>
internal static class DecisionTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>5 / 100 / 50 MB per minute for every quality: a 120 min movie may be 600 MB to 12000 MB, ideally 6000 MB.</summary>
    public static IReadOnlyDictionary<int, QualityDefinition> Definitions { get; } =
        Quality.All.ToDictionary(q => q.Id, q => new QualityDefinition(q.Id, q.Name, 5, 100, 50));

    public static QualityProfile GermanProfile(bool upgradeAllowed = true, bool preferDualLanguage = true, int? cutoffQualityId = null) => new()
    {
        Id = 1,
        Name = "Deutsch 1080p",
        Items =
        [
            new(Quality.Hdtv720p.Id, false),
            new(Quality.WebDl720p.Id, false),
            new(Quality.WebDl1080p.Id, true),
            new(Quality.WebRip1080p.Id, true),
            new(Quality.Bluray1080p.Id, true),
            new(Quality.Remux1080p.Id, true),
        ],
        CutoffQualityId = cutoffQualityId ?? Quality.Bluray1080p.Id,
        UpgradeAllowed = upgradeAllowed,
        LanguageId = Language.German.Id,
        PreferDualLanguage = preferDualLanguage,
    };

    public static QualityProfile ProfileWithLanguage(Language language) => GermanProfile(preferDualLanguage: false) with
    {
        Name = $"{language.Name} 1080p",
        LanguageId = language.Id,
    };

    public static ReleaseCandidate Candidate(
        string title,
        Quality quality,
        long sizeMb = 6000,
        Language[]? languages = null,
        Revision? revision = null,
        int indexerPriority = 25,
        DateTimeOffset? published = null,
        bool isSpecial = false,
        string indexer = "Indexer")
    {
        var release = new ParsedRelease
        {
            ReleaseTitle = title,
            Title = "Dune Part Two",
            CleanTitle = "dunepart2",
            Year = 2024,
            Quality = new QualityModel(quality, revision ?? new Revision()),
            Languages = languages ?? [Language.German],
            IsSpecial = isSpecial,
        };

        return new ReleaseCandidate(
            release,
            sizeMb * SizeRules.Megabyte,
            indexer,
            indexerPriority,
            published ?? Now.AddDays(-1),
            $"https://indexer.example/get/{title}",
            title);
    }

    public static DecisionTarget Target(
        QualityProfile? profile = null,
        MediaType mediaType = MediaType.Movie,
        int runtimeMinutes = 120,
        int episodeCount = 1,
        Language? originalLanguage = null,
        QualityModel? existing = null,
        Language[]? existingLanguages = null,
        bool monitored = true,
        string[]? blocklist = null) =>
        new(
            mediaType,
            "Dune: Part Two",
            2024,
            runtimeMinutes,
            episodeCount,
            profile ?? GermanProfile(),
            originalLanguage ?? Language.English,
            existing,
            existingLanguages ?? [],
            monitored,
            new HashSet<string>(blocklist ?? []));

    public static IReadOnlyList<Decision> Evaluate(DecisionTarget target, params ReleaseCandidate[] candidates) =>
        DecisionEngine.Evaluate(candidates, target, Definitions, Now);

    public static Decision EvaluateOne(DecisionTarget target, ReleaseCandidate candidate) =>
        Assert.Single(Evaluate(target, candidate));

    public static void AssertAccepted(Decision decision) =>
        Assert.True(decision.Accepted, $"Expected accepted, got: {string.Join("; ", decision.Rejections)}");

    public static Rejection AssertRejected(Decision decision, string code)
    {
        Assert.False(decision.Accepted, "Expected a rejection");
        return Assert.Single(decision.Rejections, r => r.Code == code);
    }
}
