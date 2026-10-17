// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Parsing;

namespace StreamZH.Tests.Parsing;

public class NameParserTests
{
    [Theory]
    [InlineData("Blade Runner 2049 (2017)", "Blade Runner 2049", 2017, null)]
    [InlineData("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968, null)]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264", "The Matrix", 1999, null)]
    [InlineData("Dune (2021) {tmdb-438631}", "Dune", 2021, 438631)]
    [InlineData("Arrival [2016] [imdbid-tt2543164]", "Arrival", 2016, null)]
    [InlineData("Amélie", "Amélie", null, null)]
    [InlineData("Alien.Romulus.2160p.WEB-DL", "Alien Romulus", null, null)]
    [InlineData("Breaking Bad", "Breaking Bad", null, null)]
    public void Parses_folder_names(string folder, string title, int? year, int? tmdbId)
    {
        var parsed = FolderNameParser.Parse(folder);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(tmdbId, parsed.TmdbId);
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E02.720p.mkv", 1, new[] { 2 })]
    [InlineData("show - s02e10 - title.mp4", 2, new[] { 10 })]
    [InlineData("Show.S01E01E02.1080p.mkv", 1, new[] { 1, 2 })]
    [InlineData("Show.S03E01-E03.mkv", 3, new[] { 1, 2, 3 })]
    [InlineData("Show 4x07 Title.avi", 4, new[] { 7 })]
    [InlineData("Show.S10E100.mkv", 10, new[] { 100 })]
    public void Parses_episode_numbers(string fileName, int season, int[] episodes)
    {
        var parsed = EpisodeFileParser.Parse(fileName);
        Assert.NotNull(parsed);
        Assert.Equal(season, parsed.Value.Season);
        Assert.Equal(episodes, parsed.Value.Episodes);
    }

    [Theory]
    [InlineData("Movie.2019.1080p.mkv")]
    [InlineData("Show.1920x1080.mkv")]
    public void Ignores_non_episodes(string fileName) => Assert.Null(EpisodeFileParser.Parse(fileName));
}