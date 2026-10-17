// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabRequestBuilderTests
{
    private static readonly MovieSearchCriteria StarWars = new() { Titles = ["Star Wars"], Year = 1977, TmdbId = 11, ImdbId = "tt0076759" };

    private static readonly EpisodeSearchCriteria MonkeyIsland = new()
    {
        Titles = ["Monkey Island"],
        TvdbId = 20,
        ImdbId = "tt0000040",
        TmdbId = 50,
        SeasonNumber = 1,
        EpisodeNumbers = [2],
    };

    private static readonly NewznabCaps Defaults = NewznabCaps.Default;

    /// <summary>Tier → query → the parameter string as it goes into the URL.</summary>
    private static List<List<string>> Movie(MovieSearchCriteria criteria, NewznabCaps caps, IReadOnlyList<int>? categories = null) =>
        Single(NewznabRequestBuilder.ForMovie(criteria, categories ?? [1, 2], caps));

    private static List<List<string>> Tv(EpisodeSearchCriteria criteria, NewznabCaps caps) =>
        Single(NewznabRequestBuilder.ForEpisodes(criteria, [1, 2], caps));

    private static List<List<string>> Single(IReadOnlyList<NewznabSearchPlan> plans)
    {
        var plan = Assert.Single(plans);
        return [.. plan.Tiers.Select(t => t.Select(q => $"t={q.Function}{q.Parameters}").ToList())];
    }

    [Fact]
    public void Builds_urls_in_radarr_order_with_distinct_categories()
    {
        var url = NewznabUrls.Build(Indexer(), "movie", [1, 2, 2, 3], 200, 100, "&tmdbid=11");

        Assert.Equal("http://127.0.0.1:1234/api?t=movie&cat=1,2,3&extended=1&apikey=abcd1234secret&offset=200&limit=100&tmdbid=11", url.ToString());
    }

    [Theory]
    [InlineData("https://indexer.example", "api", "https://indexer.example/api")]
    [InlineData("https://indexer.example/", "/api/", "https://indexer.example/api")]
    [InlineData("http://prowlarr:9696/5/", "/api", "http://prowlarr:9696/5/api")]
    [InlineData("https://indexer.example", "", "https://indexer.example")]
    public void Joins_base_url_and_api_path(string baseUrl, string apiPath, string expected) =>
        Assert.Equal(expected, NewznabUrls.ApiEndpoint(Indexer(baseUrl: baseUrl, apiPath: apiPath)));

    [Fact]
    public void Omits_api_key_when_none_is_set() =>
        Assert.DoesNotContain("apikey", NewznabUrls.Build(Indexer(apiKey: " "), "caps").ToString(), StringComparison.Ordinal);

    [Fact]
    public void Does_not_search_by_imdbid_if_not_supported()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = ["q"] });

        Assert.Equal([["t=movie&q=Star%20Wars%201977"]], tiers);
    }

    [Fact]
    public void Searches_by_imdbid_without_tt_prefix_if_supported()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = ["q", "imdbid"] });

        Assert.Equal(["t=movie&imdbid=0076759"], tiers[0]);
        Assert.Equal(["t=movie&q=Star%20Wars%201977"], tiers[1]);
    }

    [Fact]
    public void Searches_by_tmdbid_if_supported()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = ["q", "tmdbid"] });

        Assert.Equal(["t=movie&tmdbid=11"], tiers[0]);
    }

    [Fact]
    public void Prefers_tmdbid_over_imdbid()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = ["q", "tmdbid", "imdbid"] });

        Assert.Equal(["t=movie&tmdbid=11"], tiers[0]);
    }

    [Fact]
    public void Uses_aggregated_id_search_if_supported()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = ["q", "tmdbid", "imdbid"], MovieAggregateIdSearch = true });

        Assert.Equal(["t=movie&tmdbid=11&imdbid=0076759"], tiers[0]);
        Assert.Equal(["t=movie&q=Star%20Wars%201977"], tiers[1]);
    }

    [Fact]
    public void Does_not_use_aggregated_id_search_if_no_ids_are_supported_or_known()
    {
        var noIds = Movie(StarWars, Defaults with { MovieSearchParams = ["q"], MovieAggregateIdSearch = true });
        Assert.Equal([["t=movie&q=Star%20Wars%201977"]], noIds);

        var unknown = Movie(StarWars with { ImdbId = null, TmdbId = 0 }, Defaults with { MovieSearchParams = ["q", "imdbid", "tmdbid"], MovieAggregateIdSearch = true });
        Assert.Equal([["t=movie&q=Star%20Wars%201977"]], unknown);
    }

    [Fact]
    public void Falls_back_to_basic_search_when_movie_search_is_unavailable()
    {
        var tiers = Movie(StarWars, Defaults with { MovieSearchParams = null, TextSearchEngine = "raw" });

        Assert.Equal([["t=search&q=Star%20Wars%201977"]], tiers);
    }

    [Fact]
    public void No_search_without_any_supported_mode_or_categories()
    {
        Assert.Empty(NewznabRequestBuilder.ForMovie(StarWars, [1], Defaults with { MovieSearchParams = null, SearchParams = null }));
        Assert.Empty(NewznabRequestBuilder.ForMovie(StarWars, [], Defaults));
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland, [], Defaults));
    }

    [Fact]
    public void Text_search_needs_the_year_like_radarr()
    {
        var tiers = Movie(StarWars with { Year = 0 }, Defaults);

        Assert.Equal([["t=movie&imdbid=0076759"]], tiers);
    }

    [Fact]
    public void Encodes_raw_title()
    {
        var criteria = new MovieSearchCriteria { Titles = ["Some Movie & Title: Words"], Year = 2021, TmdbId = 123 };

        var tiers = Movie(criteria, Defaults with { MovieSearchParams = ["q"], MovieTextSearchEngine = "raw" });

        Assert.Equal([["t=movie&q=Some%20Movie%20%26%20Title%3A%20Words%202021"]], tiers);
    }

    [Fact]
    public void Uses_clean_title_and_encodes()
    {
        var criteria = new MovieSearchCriteria { Titles = ["Some Movie & Title: Words"], Year = 2021, TmdbId = 123 };

        var tiers = Movie(criteria, Defaults with { MovieSearchParams = ["q"], MovieTextSearchEngine = "sphinx" });

        Assert.Equal([["t=movie&q=Some%20Movie%20and%20Title%20Words%202021"]], tiers);
    }

    [Fact]
    public void Searches_every_title_once()
    {
        var criteria = StarWars with { Titles = ["Star Wars", "star wars", "Krieg der Sterne", " "] };

        var tiers = Movie(criteria, Defaults with { MovieSearchParams = ["q"] });

        Assert.Equal([["t=movie&q=Star%20Wars%201977", "t=movie&q=Krieg%20der%20Sterne%201977"]], tiers);
    }

    [Theory]
    [InlineData("The Lord of the Rings: The Return of the King", "Lord+of+the+Rings+The+Return+of+the+King")]
    [InlineData("Amélie", "Amelie")]
    [InlineData("Marvel's The Avengers", "Marvels+The+Avengers")]
    [InlineData("Mr. & Mrs. Smith", "Mr+and+Mrs+Smith")]
    [InlineData("Don’t Look Up", "Dont+Look+Up")]
    public void Cleans_titles_like_radarr(string title, string expected) =>
        Assert.Equal(expected, NewznabRequestBuilder.CleanTitle(title));

    [Theory]
    [InlineData("tt0076759", "0076759")]
    [InlineData("0076759", "0076759")]
    [InlineData("76759", "0076759")]
    [InlineData("tt10872600", "10872600")]
    [InlineData("tt0", null)]
    [InlineData("nm123", null)]
    [InlineData(null, null)]
    public void Normalises_imdb_ids(string? input, string? expected) =>
        Assert.Equal(expected, NewznabRequestBuilder.ImdbDigits(input));

    [Fact]
    public void Does_not_search_by_tv_ids_if_not_supported()
    {
        var tiers = Tv(MonkeyIsland, Defaults with { TvSearchParams = ["q", "season", "ep"] });

        Assert.Equal([["t=tvsearch&q=Monkey%20Island&season=1&ep=2"]], tiers);
    }

    [Theory]
    [InlineData("tvdbid", "&tvdbid=20")]
    [InlineData("imdbid", "&imdbid=tt0000040")]
    [InlineData("tmdbid", "&tmdbid=50")]
    public void Searches_by_supported_tv_id(string parameter, string expected)
    {
        var tiers = Tv(MonkeyIsland, Defaults with { TvSearchParams = ["q", parameter, "season", "ep"] });

        Assert.Equal([$"t=tvsearch{expected}&season=1&ep=2"], tiers[0]);
        Assert.Equal(["t=tvsearch&q=Monkey%20Island&season=1&ep=2"], tiers[1]);
    }

    [Fact]
    public void Prefers_tvdbid_then_imdbid_then_tmdbid()
    {
        var tiers = Tv(MonkeyIsland, Defaults with { TvSearchParams = ["q", "tmdbid", "imdbid", "tvdbid", "season", "ep"] });
        Assert.Equal(["t=tvsearch&tvdbid=20&season=1&ep=2"], tiers[0]);

        tiers = Tv(MonkeyIsland with { TvdbId = 0 }, Defaults with { TvSearchParams = ["q", "tmdbid", "imdbid", "tvdbid", "season", "ep"] });
        Assert.Equal(["t=tvsearch&imdbid=tt0000040&season=1&ep=2"], tiers[0]);
    }

    [Fact]
    public void Uses_aggregated_tv_id_search_if_supported()
    {
        var tiers = Tv(MonkeyIsland, Defaults with { TvSearchParams = ["q", "tvdbid", "imdbid", "tmdbid", "season", "ep"], TvAggregateIdSearch = true });

        Assert.Equal(["t=tvsearch&tvdbid=20&imdbid=tt0000040&tmdbid=50&season=1&ep=2"], tiers[0]);
    }

    [Fact]
    public void Does_not_use_aggregated_tv_id_search_if_no_ids_are_known()
    {
        var tiers = Tv(MonkeyIsland with { TvdbId = 0, ImdbId = null, TmdbId = 0 }, Defaults with { TvSearchParams = ["q", "tvdbid", "season", "ep"], TvAggregateIdSearch = true });

        Assert.Equal([["t=tvsearch&q=Monkey%20Island&season=1&ep=2"]], tiers);
    }

    [Fact]
    public void Falls_back_to_title_parameter_when_supported()
    {
        var criteria = MonkeyIsland with { Titles = ["Elith & Little"] };

        var tiers = Tv(criteria, Defaults with { TvSearchParams = ["q", "title", "tvdbid", "season", "ep"] });

        Assert.Equal(["t=tvsearch&tvdbid=20&season=1&ep=2"], tiers[0]);
        Assert.Equal(["t=tvsearch&title=Elith%20%26%20Little&season=1&ep=2"], tiers[1]);
    }

    [Fact]
    public void Season_pack_searches_with_season_only()
    {
        var tiers = Tv(MonkeyIsland with { SeasonNumber = 3, EpisodeNumbers = [] }, Defaults with { TvSearchParams = ["q", "tvdbid", "season", "ep"] });

        Assert.Equal(["t=tvsearch&tvdbid=20&season=3"], tiers[0]);
        Assert.Equal(["t=tvsearch&q=Monkey%20Island&season=3"], tiers[1]);
    }

    [Fact]
    public void Season_zero_is_sent_as_00()
    {
        var tiers = Tv(MonkeyIsland with { SeasonNumber = 0, EpisodeNumbers = [5] }, Defaults with { TvSearchParams = ["tvdbid", "season", "ep"] });

        Assert.Equal([["t=tvsearch&tvdbid=20&season=00&ep=5"]], tiers);
    }

    [Fact]
    public void Daily_episode_uses_year_as_season_and_month_day_as_episode()
    {
        var criteria = MonkeyIsland with { AirDate = "2026-03-07", EpisodeNumbers = [] };

        var tiers = Tv(criteria, Defaults with { TvSearchParams = ["q", "tvdbid", "season", "ep"] });

        Assert.Equal(["t=tvsearch&tvdbid=20&season=2026&ep=03/07"], tiers[0]);
        Assert.Equal(["t=tvsearch&q=Monkey%20Island&season=2026&ep=03/07"], tiers[1]);
    }

    [Fact]
    public void Several_episodes_make_one_plan_per_episode()
    {
        var plans = NewznabRequestBuilder.ForEpisodes(MonkeyIsland with { EpisodeNumbers = [2, 3, 3] }, [5000], Defaults with { TvSearchParams = ["tvdbid", "season", "ep"] });

        Assert.Equal(2, plans.Count);
        Assert.Equal("&tvdbid=20&season=1&ep=2", plans[0].Tiers[0][0].Parameters);
        Assert.Equal("&tvdbid=20&season=1&ep=3", plans[1].Tiers[0][0].Parameters);
    }

    [Fact]
    public void No_tv_search_without_needed_parameters()
    {
        // Episode searches need season and ep, season packs need season, and some id or text parameter must exist.
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland, [1], Defaults with { TvSearchParams = ["q", "season"] }));
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland with { EpisodeNumbers = [] }, [1], Defaults with { TvSearchParams = ["q", "ep"] }));
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland, [1], Defaults with { TvSearchParams = ["season", "ep"] }));
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland, [1], Defaults with { TvSearchParams = null }));
        Assert.Empty(NewznabRequestBuilder.ForEpisodes(MonkeyIsland with { AirDate = "07.03.2026" }, [1], Defaults));
    }

    [Fact]
    public void Uses_clean_title_for_tv_query_unless_raw()
    {
        var criteria = MonkeyIsland with { Titles = ["The Office (US)"], TvdbId = 0, ImdbId = null, TmdbId = 0 };

        Assert.Equal([["t=tvsearch&q=Office%20US&season=1&ep=2"]], Tv(criteria, Defaults));
        Assert.Equal([["t=tvsearch&q=The%20Office%20%28US%29&season=1&ep=2"]], Tv(criteria, Defaults with { TvTextSearchEngine = "raw" }));
    }
}
