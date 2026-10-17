// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing;
using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.Decisions;

/// <summary>End to end: real release names through the parsers into the decision engine with the seeded profiles.</summary>
public sealed class ReleasePipelineTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<int, QualityDefinition> Definitions = QualityDefaults.Definitions.ToDictionary(d => d.QualityId);

    private static QualityProfile Profile(int id) => QualityDefaults.Profiles.Single(p => p.Id == id);

    private static DecisionTarget Movie(QualityProfile profile, QualityModel? existing = null, IReadOnlyList<Language>? existingLanguages = null) =>
        new(MediaType.Movie, "Dune: Part Two", 2024, 166, 0, profile, Language.English, existing, existingLanguages ?? [], true, new HashSet<string>());

    private static ReleaseCandidate Candidate(string name, double sizeGb = 12) =>
        new(ReleaseParser.Parse(name) ?? throw new InvalidOperationException($"Could not parse {name}"),
            (long)(sizeGb * Gb), "Test", 25, Now.AddHours(-1), "http://indexer/nzb", name);

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-DETAiLS", "Dune Part Two", 2024, 7, "DETAiLS")]
    [InlineData("Dune.Part.Two.2024.German.DL.2160p.UHD.BluRay.HDR.x265-GROUP", "Dune Part Two", 2024, 19, "GROUP")]
    [InlineData("Oppenheimer.2023.German.DTS.DL.1080p.WEB.H264-ZeroTwo", "Oppenheimer", 2023, 3, "ZeroTwo")]
    [InlineData("Die.Verurteilten.1994.German.AC3.1080p.BluRay.x264-SPiCY", "Die Verurteilten", 1994, 7, "SPiCY")]
    [InlineData("Barbie.2023.GERMAN.720p.WEB.h264-WvF", "Barbie", 2023, 5, "WvF")]
    public void Movie_names_are_parsed_completely(string name, string title, int year, int qualityId, string group)
    {
        var parsed = ReleaseParser.Parse(name);

        Assert.NotNull(parsed);
        Assert.False(parsed.IsEpisode);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(qualityId, parsed.Quality.Quality.Id);
        Assert.Equal(group, parsed.ReleaseGroup);
        Assert.Contains(Language.German, parsed.Languages);
    }

    [Fact]
    public void Series_name_is_parsed_completely()
    {
        var parsed = ReleaseParser.Parse("The.Bear.S03E05.German.DL.1080p.WEB.h264-WAYNE");

        Assert.NotNull(parsed);
        Assert.Equal("The Bear", parsed.Title);
        Assert.Equal(3, parsed.SeasonNumber);
        Assert.Equal([5], parsed.EpisodeNumbers);
        Assert.Equal(Quality.WebDl1080p, parsed.Quality.Quality);
        Assert.Equal("WAYNE", parsed.ReleaseGroup);
        Assert.Contains(Language.German, parsed.Languages);
    }

    [Fact]
    public void German_dl_beats_german_only_at_the_same_quality()
    {
        var decisions = DecisionEngine.Evaluate(
            [
                Candidate("Dune.Part.Two.2024.German.AC3.1080p.BluRay.x264-AAA"),
                Candidate("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-BBB"),
            ],
            Movie(Profile(QualityDefaults.DefaultProfileId)), Definitions, Now);

        Assert.All(decisions, d => Assert.True(d.Accepted, string.Join(", ", d.Rejections)));
        Assert.Equal("BBB", decisions[0].Candidate.Release.ReleaseGroup);
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.1080p.BluRay.x264-SPARKS")]
    [InlineData("Dune.Part.Two.2024.German.Subbed.1080p.WEB.h264-SUBS")]
    [InlineData("Dune.Part.Two.2024.FRENCH.1080p.WEB.h264-FR")]
    public void Deutsch_profile_rejects_releases_without_german_audio(string name)
    {
        var decision = DecisionEngine.Evaluate([Candidate(name)], Movie(Profile(QualityDefaults.DefaultProfileId)), Definitions, Now).Single();

        Assert.False(decision.Accepted);
        Assert.Contains(decision.Rejections, r => r.Code == RejectionCodes.WantedLanguage);
    }

    [Fact]
    public void Deutsch_1080p_rejects_2160p_and_cam()
    {
        var decisions = DecisionEngine.Evaluate(
            [
                Candidate("Dune.Part.Two.2024.German.DL.2160p.UHD.BluRay.x265-UHD", 40),
                Candidate("Dune.Part.Two.2024.German.CAM.x264-CAM", 2),
            ],
            Movie(Profile(QualityDefaults.DefaultProfileId)), Definitions, Now);

        Assert.All(decisions, d => Assert.Contains(d.Rejections, r => r.Code == RejectionCodes.QualityNotWanted));
    }

    [Fact]
    public void Original_language_profile_takes_an_untagged_english_release()
    {
        var decision = DecisionEngine.Evaluate([Candidate("Dune.Part.Two.2024.1080p.BluRay.x264-SPARKS")], Movie(Profile(2)), Definitions, Now).Single();

        Assert.True(decision.Accepted, string.Join(", ", decision.Rejections));
    }

    [Fact]
    public void German_release_replaces_an_english_file_even_at_the_same_quality()
    {
        var existing = new QualityModel(Quality.Bluray1080p);
        var decision = DecisionEngine.Evaluate(
            [Candidate("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-DETAiLS")],
            Movie(Profile(QualityDefaults.DefaultProfileId), existing, [Language.English]), Definitions, Now).Single();

        Assert.True(decision.Accepted, string.Join(", ", decision.Rejections));
    }

    [Fact]
    public void German_dl_file_at_the_cutoff_is_not_replaced()
    {
        var existing = new QualityModel(Quality.Bluray1080p);
        var decision = DecisionEngine.Evaluate(
            [Candidate("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-OTHER")],
            Movie(Profile(QualityDefaults.DefaultProfileId), existing, [Language.German, Language.English]), Definitions, Now).Single();

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void Proper_replaces_the_same_quality()
    {
        var existing = new QualityModel(Quality.WebDl1080p);
        var decision = DecisionEngine.Evaluate(
            [Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.H264.PROPER-GROUP", 8)],
            Movie(Profile(QualityDefaults.DefaultProfileId), existing, [Language.German, Language.English]), Definitions, Now).Single();

        Assert.True(decision.Accepted, string.Join(", ", decision.Rejections));
    }

    [Fact]
    public void Samples_are_rejected()
    {
        var decision = DecisionEngine.Evaluate(
            [Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.H264.SAMPLE-GROUP", 0.05)],
            Movie(Profile(QualityDefaults.DefaultProfileId)), Definitions, Now).Single();

        Assert.Contains(decision.Rejections, r => r.Code == RejectionCodes.Sample);
    }

    [Fact]
    public void Oversized_web_release_is_rejected()
    {
        // WEB-1080p allows at most 100 MB per minute: 166 min → about 16.2 GB.
        var decision = DecisionEngine.Evaluate(
            [Candidate("Dune.Part.Two.2024.German.DL.1080p.WEB.H264-GROUP", 25)],
            Movie(Profile(QualityDefaults.DefaultProfileId)), Definitions, Now).Single();

        Assert.Contains(decision.Rejections, r => r.Code == RejectionCodes.AboveMaximumSize);
    }

    [Fact]
    public void Parse_quality_matches_quality_parser_for_movie_names()
    {
        const string name = "Dune.Part.Two.2024.German.DL.2160p.WEB.H265-GROUP";
        Assert.Equal(QualityParser.ParseQuality(name), ReleaseParser.Parse(name)!.Quality);
    }
}

public sealed class ReleaseMatcherTests
{
    private static ParsedRelease Parse(string name) => ReleaseParser.Parse(name)!;

    [Theory]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-DETAiLS", "Dune: Part Two", 2024, ReleaseMatch.Match)]
    [InlineData("Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-DETAiLS", "Arrival", 2016, ReleaseMatch.WrongTitle)]
    [InlineData("Dune.Part.Two.2023.German.DL.1080p.BluRay.x264-DETAiLS", "Dune: Part Two", 2024, ReleaseMatch.Match)]
    [InlineData("Dune.1984.German.DL.1080p.BluRay.x264-OLD", "Dune", 2021, ReleaseMatch.WrongYear)]
    [InlineData("Die.Schoene.und.das.Biest.2017.German.DL.1080p.BluRay.x264-GRP", "Die Schöne und das Biest", 2017, ReleaseMatch.Match)]
    [InlineData("Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP", "Breaking Bad", 2008, ReleaseMatch.WrongType)]
    public void Movies(string name, string title, int year, ReleaseMatch expected) =>
        Assert.Equal(expected, ReleaseMatcher.Match(Parse(name), isSeries: false, [title], year));

    [Fact]
    public void Original_title_matches_too() =>
        Assert.Equal(ReleaseMatch.Match, ReleaseMatcher.Match(
            Parse("The.Shawshank.Redemption.1994.1080p.BluRay.x264-GRP"), isSeries: false, ["Die Verurteilten", "The Shawshank Redemption"], 1994));

    [Fact]
    public void Tmdb_id_in_the_name_wins_over_the_title() =>
        Assert.Equal(ReleaseMatch.Match, ReleaseMatcher.Match(
            Parse("Some.Other.Name.2024.1080p.WEB.h264-GRP {tmdb-693134}"), isSeries: false, ["Dune: Part Two"], 2024, tmdbId: 693134));

    [Theory]
    [InlineData("Breaking.Bad.S01E02.German.DL.720p.WEB.h264-GRP", "Breaking Bad", 2008, ReleaseMatch.Match)]
    [InlineData("Doctor.Who.2005.S01E01.720p.WEB.h264-GRP", "Doctor Who", 2005, ReleaseMatch.Match)]
    [InlineData("Doctor.Who.S01E01.720p.WEB.h264-GRP", "Doctor Who", 2005, ReleaseMatch.Match)]
    [InlineData("Doctor.Who.2005.S01E01.720p.WEB.h264-GRP", "Doctor Who", 1963, ReleaseMatch.WrongYear)]
    [InlineData("The.Bear.S03E05.German.DL.1080p.WEB.h264-WAYNE", "Breaking Bad", 2008, ReleaseMatch.WrongTitle)]
    public void Series(string name, string title, int year, ReleaseMatch expected) =>
        Assert.Equal(expected, ReleaseMatcher.Match(Parse(name), isSeries: true, [title], year));
}
