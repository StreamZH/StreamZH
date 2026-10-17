// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

public class TmdbClientTests
{
    [Fact]
    public async Task Uses_bearer_token_and_parses_movie()
    {
        var handler = new StubHandler(_ => """
            {"id":438631,"title":"Dune","original_title":"Dune","original_language":"en","release_date":"2021-09-15","runtime":155,"status":"Released",
             "imdb_id":"tt1160419","overview":"Paul Atreides...","genres":[{"id":878,"name":"Science Fiction"}],
             "poster_path":"/d5NXSklXo0qyIYkgV94XAgMIckC.jpg","backdrop_path":"/back.jpg"}
            """);
        var client = new TmdbClient(new HttpClient(handler), new Settings("eyJhbGciOiJIUzI1NiJ9.long-read-access-token", "de-DE"));

        var movie = await client.GetMovieAsync(438631);

        Assert.Equal("Dune", movie.Title);
        Assert.Equal(2021, movie.Year);
        Assert.Equal(155, movie.Runtime);
        Assert.Equal(["Science Fiction"], movie.Genres);
        Assert.Equal("en", movie.OriginalLanguage);
        Assert.Equal(StreamZH.Core.Quality.Language.English, StreamZH.Core.Quality.Language.FromIsoCode(movie.OriginalLanguage));
        Assert.Equal(StreamZH.Core.Quality.Language.German, StreamZH.Core.Quality.Language.FromIsoCode("de"));
        Assert.Equal(StreamZH.Core.Quality.Language.Unknown, StreamZH.Core.Quality.Language.FromIsoCode("xx"));
        Assert.Equal("Bearer", handler.Last!.Headers.Authorization!.Scheme);
        Assert.Contains("language=de-DE", handler.Last.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uses_api_key_parameter_for_v3_keys_and_parses_series()
    {
        var handler = new StubHandler(_ => """
            {"id":1396,"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","status":"Ended",
             "networks":[{"name":"AMC"}],"genres":[{"name":"Drama"}],"seasons":[{"season_number":1},{"season_number":0}],
             "external_ids":{"imdb_id":"tt0903747","tvdb_id":81189}}
            """);
        var client = new TmdbClient(new HttpClient(handler), new Settings("0123456789abcdef0123456789abcdef", "en-US"));

        var series = await client.GetSeriesAsync(1396);

        Assert.Equal(81189, series.TvdbId);
        Assert.Equal("AMC", series.Network);
        Assert.Equal([0, 1], series.SeasonNumbers);
        Assert.Null(handler.Last!.Headers.Authorization);
        Assert.Contains("api_key=0123456789abcdef0123456789abcdef", handler.Last.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_rejected_key_and_missing_setup()
    {
        var rejected = new TmdbClient(new HttpClient(new StubHandler(_ => null, HttpStatusCode.Unauthorized)), new Settings("bad", "en-US"));
        var ex = await Assert.ThrowsAsync<TmdbException>(() => rejected.SearchMoviesAsync("Dune", null));
        Assert.Equal(401, ex.StatusCode);

        var empty = new TmdbClient(new HttpClient(new StubHandler(_ => "{}")), new Settings(null, "en-US"));
        Assert.False(empty.IsConfigured);
        await Assert.ThrowsAsync<TmdbException>(() => empty.SearchMoviesAsync("Dune", null));
    }

    private sealed record Settings(string? Token, string Language) : ITmdbSettings;

    private sealed class StubHandler(Func<HttpRequestMessage, string?> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            var body = respond(request) ?? "{}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}