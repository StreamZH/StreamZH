// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

/// <summary>The TMDB calls behind Discover (0.10), against the fake TMDB.</summary>
public class TmdbDiscoverClientTests
{
    private const string Page = """
        {"page":2,"total_pages":900,"results":[
          {"id":438631,"title":"Dune","release_date":"2021-09-15","poster_path":"/dune.jpg","backdrop_path":"/dune-bg.jpg","overview":"Paul Atreides...","vote_average":7.8,"vote_count":12000}]}
        """;

    [Theory]
    [InlineData(TmdbMediaKind.Movie, TmdbChart.Trending, "/trending/movie/week")]
    [InlineData(TmdbMediaKind.Series, TmdbChart.Trending, "/trending/tv/week")]
    [InlineData(TmdbMediaKind.Movie, TmdbChart.Popular, "/movie/popular")]
    [InlineData(TmdbMediaKind.Series, TmdbChart.TopRated, "/tv/top_rated")]
    [InlineData(TmdbMediaKind.Movie, TmdbChart.Upcoming, "/discover/movie")]
    [InlineData(TmdbMediaKind.Series, TmdbChart.Upcoming, "/discover/tv")]
    public async Task Charts_read_the_right_list_with_overview_backdrop_and_at_most_500_pages(TmdbMediaKind kind, TmdbChart chart, string path)
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer(path, Page);

        var page = await tmdb.Client.GetChartAsync(kind, chart, 2);

        Assert.Equal((2, 500), (page.Page, page.TotalPages));
        var entry = Assert.Single(page.Results);
        Assert.Equal((kind, 438631, "Paul Atreides...", "/dune-bg.jpg", "/dune.jpg"), (entry.Kind, entry.TmdbId, entry.Overview, entry.BackdropPath, entry.PosterPath));
        Assert.Contains("page=2", tmdb.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("language=en-US", tmdb.Requests[0].Query, StringComparison.Ordinal);
        if (chart == TmdbChart.Upcoming)
        {
            Assert.Contains(kind == TmdbMediaKind.Movie ? "primary_release_date.gte=" : "first_air_date.gte=", tmdb.Requests[0].Query, StringComparison.Ordinal);
            Assert.Contains("sort_by=popularity.desc", tmdb.Requests[0].Query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_mixes_movies_and_shows_and_drops_people()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/search/multi", """
            {"page":1,"total_pages":1,"results":[
              {"media_type":"movie","id":438631,"title":"Dune","release_date":"2021-09-15"},
              {"media_type":"tv","id":90228,"name":"Dune: Prophecy","first_air_date":"2024-11-17"},
              {"media_type":"person","id":31,"name":"Jane Doe"}]}
            """);
        tmdb.Answer("/search/tv", """{"page":1,"total_pages":1,"results":[{"id":90228,"name":"Dune: Prophecy","first_air_date":"2024-11-17"}]}""");

        var mixed = await tmdb.Client.SearchAsync(null, " dune & sand ", 1);
        var shows = await tmdb.Client.SearchAsync(TmdbMediaKind.Series, "dune", 1);

        Assert.Equal([(TmdbMediaKind.Movie, 438631), (TmdbMediaKind.Series, 90228)], mixed.Results.Select(r => (r.Kind, r.TmdbId)));
        Assert.Contains("query=dune%20%26%20sand", tmdb.Requests[0].Query, StringComparison.Ordinal);
        Assert.Equal(TmdbMediaKind.Series, Assert.Single(shows.Results).Kind);
    }

    [Fact]
    public async Task Series_details_list_the_episode_count_and_air_date_of_each_season()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/tv/1396", """
            {"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20","seasons":[
              {"season_number":1,"name":"Season 1","episode_count":7,"air_date":"2008-01-20","poster_path":"/s1.jpg"},
              {"season_number":0,"name":"Specials","episode_count":9}]}
            """);

        var details = await ((ITmdbDiscoverClient)tmdb.Client).GetSeriesDetailsAsync(1396, "US");

        Assert.Equal(
            [new TmdbSeasonSummary(0, "Specials", null, 9, null), new TmdbSeasonSummary(1, "Season 1", "/s1.jpg", 7, "2008-01-20")],
            details.Seasons);
    }
}
