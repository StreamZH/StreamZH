// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

/// <summary>The TMDB calls of the import lists, against a fake TMDB that answers by path.</summary>
public class TmdbListClientTests
{
    [Fact]
    public async Task Reads_a_mixed_list_with_its_name_and_pages()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/list/8245", """
            {"name":"Best sci-fi","page":1,"total_pages":3,"items":[
              {"media_type":"movie","id":438631,"title":"Dune","release_date":"2021-09-15","vote_average":7.8,"vote_count":12000,"original_language":"en"},
              {"media_type":"tv","id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20"},
              {"media_type":"person","id":31,"name":"Tom Hanks"}]}
            """);

        var page = await tmdb.Client.GetListAsync("8245", 1);

        Assert.Equal("Best sci-fi", page.Name);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Results.Count);
        var dune = page.Results[0];
        Assert.Equal((TmdbMediaKind.Movie, 438631, "Dune", 2021, "2021-09-15", 7.8, 12000, "en"),
            (dune.Kind, dune.TmdbId, dune.Title, dune.Year, dune.ReleaseDate, dune.VoteAverage, dune.VoteCount, dune.OriginalLanguage));
        Assert.Equal((TmdbMediaKind.Series, "Breaking Bad", 2008), (page.Results[1].Kind, page.Results[1].Title, page.Results[1].Year));
        Assert.Contains("page=1", tmdb.Requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_collections_people_companies_and_keywords()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/collection/10", """{"id":10,"name":"Star Wars Collection","parts":[{"id":11,"title":"Star Wars","release_date":"1977-05-25"},{"id":1891,"title":"The Empire Strikes Back","release_date":"1980-05-20"}]}""");
        tmdb.Answer("/person/525", """
            {"id":525,"name":"Christopher Nolan",
             "movie_credits":{"cast":[{"id":1,"title":"Cameo","release_date":"2001-01-01"}],"crew":[{"id":27205,"title":"Inception","release_date":"2010-07-15","department":"Directing","job":"Director"}]},
             "tv_credits":{"cast":[],"crew":[{"id":99,"name":"Some Show","first_air_date":"2020-01-01","department":"Production","job":"Producer"}]}}
            """);
        tmdb.Answer("/company/41077", """{"id":41077,"name":"A24"}""");
        tmdb.Answer("/keyword/4379", """{"id":4379,"name":"time travel"}""");
        tmdb.Answer("/movie/11", """{"id":11,"belongs_to_collection":{"id":10,"name":"Star Wars Collection"}}""");
        tmdb.Answer("/movie/12", """{"id":12,"belongs_to_collection":null}""");

        var collection = await tmdb.Client.GetCollectionAsync(10);
        var person = await tmdb.Client.GetPersonCreditsAsync(525);

        Assert.Equal("Star Wars Collection", collection.Name);
        Assert.Equal([11, 1891], collection.Results.Select(r => r.TmdbId));
        Assert.All(collection.Results, r => Assert.Equal(TmdbMediaKind.Movie, r.Kind));
        Assert.Equal("Christopher Nolan", person.Name);
        Assert.Equal("Cameo", Assert.Single(person.Cast).Entry.Title);
        Assert.Null(person.Cast[0].Department);
        Assert.Equal(["Directing", "Production"], person.Crew.Select(c => c.Department));
        Assert.Equal(TmdbMediaKind.Series, person.Crew[1].Entry.Kind);
        Assert.Contains("append_to_response=movie_credits,tv_credits", tmdb.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal("A24", await tmdb.Client.GetCompanyNameAsync(41077));
        Assert.Equal("time travel", await tmdb.Client.GetKeywordNameAsync(4379));
        Assert.Equal((10, "Star Wars Collection"), await tmdb.Client.GetMovieCollectionAsync(11));
        Assert.Null(await tmdb.Client.GetMovieCollectionAsync(12));
    }

    [Fact]
    public async Task Discover_sends_the_parameters_and_find_maps_imdb_and_tvdb_ids()
    {
        var tmdb = new FakeTmdbHttp();
        tmdb.Answer("/discover/tv", """{"page":2,"total_pages":5,"results":[{"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20"}]}""");
        tmdb.Answer("/find/tt1160419", """{"movie_results":[{"id":438631,"title":"Dune","release_date":"2021-09-15"}],"tv_results":[]}""");

        var page = await tmdb.Client.DiscoverAsync(
            TmdbMediaKind.Series, new Dictionary<string, string> { ["with_companies"] = "41077", ["vote_average.gte"] = "6.5", ["empty"] = "" }, 2);
        var found = await tmdb.Client.FindAsync("tt1160419", TmdbExternalSource.Imdb);

        var query = Uri.UnescapeDataString(tmdb.Requests[0].Query);
        Assert.Contains("with_companies=41077", query, StringComparison.Ordinal);
        Assert.Contains("vote_average.gte=6.5", query, StringComparison.Ordinal);
        Assert.Contains("page=2", query, StringComparison.Ordinal);
        Assert.Contains("include_adult=false", query, StringComparison.Ordinal);
        Assert.DoesNotContain("empty=", query, StringComparison.Ordinal);
        Assert.Equal(TmdbMediaKind.Series, Assert.Single(page.Results).Kind);
        Assert.Equal((2, 5), (page.Page, page.TotalPages));
        Assert.Equal(438631, Assert.Single(found.Movies).TmdbId);
        Assert.Empty(found.Series);
        Assert.Contains("external_source=imdb_id", tmdb.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_list_is_reported_as_not_found()
    {
        var tmdb = new FakeTmdbHttp();
        var ex = await Assert.ThrowsAsync<TmdbException>(() => tmdb.Client.GetListAsync("999", 1));
        Assert.Equal(404, ex.StatusCode);
    }
}

/// <summary>A fake TMDB: answers registered paths (without /3), 404 for everything else; records the requests.</summary>
public sealed class FakeTmdbHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<Uri, string>> _answers = new(StringComparer.Ordinal);

    public FakeTmdbHttp()
    {
        Client = new TmdbClient(new HttpClient(this) { BaseAddress = TmdbClient.BaseAddress }, new FakeSettings());
    }

    public TmdbClient Client { get; }

    public List<Uri> Requests { get; } = [];

    public void Answer(string path, string json) => _answers[path] = _ => json;

    public void Answer(string path, Func<Uri, string> json) => _answers[path] = json;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);
        var path = uri.AbsolutePath.StartsWith("/3/", StringComparison.Ordinal) ? uri.AbsolutePath[2..] : uri.AbsolutePath;
        return Task.FromResult(_answers.TryGetValue(path, out var answer)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(uri), Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    private sealed class FakeSettings : ITmdbSettings
    {
        public string? Token => "eyJhbGciOiJIUzI1NiJ9.long-read-access-token";

        public string Language => "en-US";
    }
}
