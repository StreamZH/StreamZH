// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.Titles;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Library.Titles;

public class AlternativeTitleRulesTests
{
    private static AlternativeTitle Title(string title, string source, Language? language = null, int? season = null) => new()
    {
        MediaType = MediaType.Series,
        ItemId = 1,
        Title = title,
        CleanTitle = AlternativeTitleRules.CleanTitle(title),
        Source = source,
        LanguageId = language?.Id,
        SeasonNumber = season,
    };

    [Fact]
    public void Search_titles_put_the_profile_language_first_and_stop_at_three()
    {
        AlternativeTitle[] titles =
        [
            Title("La casa de papel (ES)", AlternativeTitleSources.Translation, Language.Spanish),
            Title("Haus des Geldes", AlternativeTitleSources.Translation, Language.German),
            Title("Haus des Geldes Korea", AlternativeTitleSources.User, season: 1),
            Title("Money Heist Part 5", AlternativeTitleSources.Xem, season: 5),
        ];

        Assert.Equal(
            ["Haus des Geldes", "Money Heist", "Haus des Geldes Korea"],
            AlternativeTitleRules.SearchTitles("Money Heist", "La casa de papel", titles, Language.German.Id, true, seasonNumber: 1));
        Assert.Equal(
            ["Haus des Geldes", "Money Heist", "Money Heist Part 5"],
            AlternativeTitleRules.SearchTitles("Money Heist", "La casa de papel", titles, Language.German.Id, true, seasonNumber: 5));
        Assert.Equal(
            ["La casa de papel (ES)", "Money Heist", "Haus des Geldes Korea"],
            AlternativeTitleRules.SearchTitles("Money Heist", "La casa de papel", titles, Language.Spanish.Id, true, seasonNumber: 1));
        Assert.Equal(
            ["Money Heist", "Haus des Geldes Korea", "La casa de papel"],
            AlternativeTitleRules.SearchTitles("Money Heist", "La casa de papel", titles, Language.Any.Id, true, seasonNumber: 1));
        Assert.Equal(
            ["Money Heist", "La casa de papel"],
            AlternativeTitleRules.SearchTitles("Money Heist", "La casa de papel", titles, Language.German.Id, false));
        Assert.Equal(["Dune"], AlternativeTitleRules.SearchTitles("Dune", "Dune", [], Language.German.Id, true));
    }

    [Fact]
    public void Tmdb_titles_are_filtered_and_get_a_language()
    {
        var titles = new TmdbTitles(
            [new("Die Simpsons", "AT", null), new("Симпсоны", "RU", null), new("The Simpsons", "US", null), new("Los Simpson", "MX", null)],
            [new("de", "CH", "Die Simpsons"), new("ja", "JP", "ザ・シンプソンズ"), new("pt", "BR", "Os Simpsons"), new("fr", "FR", "Les Simpson")]);

        var (tmdb, translations) = AlternativeTitleRules.FromTmdb(MediaType.Series, 7, titles, "The Simpsons", "The Simpsons", "en", "pt-BR", []);

        Assert.Equal(["Die Simpsons:4", "Os Simpsons:30"], translations.Select(t => $"{t.Title}:{t.LanguageId}"));
        Assert.Equal(["Los Simpson:37"], tmdb.Select(t => $"{t.Title}:{t.LanguageId}"));
        Assert.All(tmdb.Concat(translations), t => Assert.Equal((MediaType.Series, 7), (t.MediaType, t.ItemId)));
        Assert.Equal(Language.German.Id, AlternativeTitleRules.LanguageOfCountry("ch"));
        Assert.Null(AlternativeTitleRules.LanguageOfCountry("XX"));
        Assert.Equal("de", AlternativeTitleRules.LanguageCode("de-CH"));
    }
}
