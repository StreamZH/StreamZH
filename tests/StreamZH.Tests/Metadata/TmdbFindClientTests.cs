// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

/// <summary>TMDB's /find for Sonarr series without a TMDB id (TVDB id, then IMDb id).</summary>
public class TmdbFindClientTests
{
    [Fact]
    public async Task Finds_a_show_by_its_TVDB_id()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/find/71256", """
            {"movie_results":[],"person_results":[],"tv_results":[{"id":2224,"name":"The Daily Show","first_air_date":"1996-07-22","original_language":"en","media_type":"tv"}],
             "tv_episode_results":[],"tv_season_results":[]}
            """);

        var found = await tmdb.Client.FindByTvdbIdAsync(71256);

        Assert.Equal((TmdbMediaKind.Series, 2224, "The Daily Show", 1996), (found!.Kind, found.TmdbId, found.Title, found.Year));
        Assert.Contains("external_source=tvdb_id", tmdb.Requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_by_IMDb_id_of_the_wanted_kind_and_returns_null_without_a_match()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/find/tt0115147", """{"movie_results":[{"id":1,"title":"Not this"}],"tv_results":[{"id":2224,"name":"The Daily Show","first_air_date":"1996-07-22"}]}""");
        tmdb.Answer("/find/999999", """{"movie_results":[],"tv_results":[]}""");

        Assert.Equal(2224, (await tmdb.Client.FindByImdbIdAsync("tt0115147", TmdbMediaKind.Series))?.TmdbId);
        Assert.Equal(1, (await tmdb.Client.FindByImdbIdAsync("tt0115147", TmdbMediaKind.Movie))?.TmdbId);
        Assert.Null(await tmdb.Client.FindByTvdbIdAsync(999999));
        Assert.Null(await tmdb.Client.FindByTvdbIdAsync(0));
        Assert.Null(await tmdb.Client.FindByImdbIdAsync("", TmdbMediaKind.Series));
        Assert.Contains("external_source=imdb_id", tmdb.Requests[0].Query, StringComparison.Ordinal);
        Assert.Equal(3, tmdb.Requests.Count);
    }
}
