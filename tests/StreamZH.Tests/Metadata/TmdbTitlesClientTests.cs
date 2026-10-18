// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

public class TmdbTitlesClientTests
{
    [Fact]
    public async Task Reads_alternative_titles_and_translations_of_a_movie()
    {
        var handler = new PathHandler(new()
        {
            ["/3/movie/1100988/alternative_titles"] = """
                {"id":1100988,"titles":[
                  {"iso_3166_1":"de","title":"Der Teufel trägt Prada 2","type":""},
                  {"iso_3166_1":"US","title":"Prada 2","type":"working title"},
                  {"iso_3166_1":"BR","title":"","type":""}]}
                """,
            ["/3/movie/1100988/translations"] = """
                {"id":1100988,"translations":[
                  {"iso_3166_1":"DE","iso_639_1":"de","name":"Deutsch","english_name":"German","data":{"title":"Der Teufel trägt Prada 2","overview":"…"}},
                  {"iso_3166_1":"CH","iso_639_1":"de","name":"Deutsch","english_name":"German","data":{"title":"","overview":""}},
                  {"iso_3166_1":"US","iso_639_1":"EN","name":"English","english_name":"English","data":{"title":"The Devil Wears Prada 2"}}]}
                """,
        });
        var client = new TmdbClient(new HttpClient(handler), new Settings("eyJ.token", "de-DE"));

        var titles = await client.GetMovieTitlesAsync(1100988);

        Assert.Equal(
            [new TmdbAlternativeTitle("Der Teufel trägt Prada 2", "DE", null), new TmdbAlternativeTitle("Prada 2", "US", "working title")],
            titles.AlternativeTitles);
        Assert.Equal(
            [new TmdbTranslation("de", "DE", "Der Teufel trägt Prada 2"), new TmdbTranslation("en", "US", "The Devil Wears Prada 2")],
            titles.Translations);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization!.Scheme));
    }

    [Fact]
    public async Task Reads_the_tv_shape_results_and_name()
    {
        var handler = new PathHandler(new()
        {
            ["/3/tv/71446/alternative_titles"] = """
                {"id":71446,"results":[{"iso_3166_1":"DE","title":"Haus des Geldes","type":""},{"iso_3166_1":"US","title":"Money Heist","type":""}]}
                """,
            ["/3/tv/71446/translations"] = """
                {"id":71446,"translations":[{"iso_3166_1":"DE","iso_639_1":"de","data":{"name":"Haus des Geldes","overview":""}},
                  {"iso_3166_1":"ES","iso_639_1":"es","data":{"name":"La casa de papel"}}]}
                """,
        });
        var client = new TmdbClient(new HttpClient(handler) { BaseAddress = new Uri("http://fake.local/3/") }, new Settings("0123456789abcdef0123456789abcdef", "en-US"));

        var titles = await client.GetSeriesTitlesAsync(71446);

        Assert.Equal(["Haus des Geldes", "Money Heist"], titles.AlternativeTitles.Select(t => t.Title));
        Assert.Equal(["de:Haus des Geldes", "es:La casa de papel"], titles.Translations.Select(t => $"{t.Language}:{t.Title}"));
        Assert.All(handler.Requests, r => Assert.StartsWith("http://fake.local/3/tv/71446/", r.RequestUri!.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_item_is_a_tmdb_404()
    {
        var client = new TmdbClient(new HttpClient(new PathHandler([])), new Settings("eyJ.token", "en-US"));

        var ex = await Assert.ThrowsAsync<TmdbException>(() => client.GetMovieTitlesAsync(1));

        Assert.Equal(404, ex.StatusCode);
    }

    private sealed record Settings(string? Token, string Language) : ITmdbSettings;

    private sealed class PathHandler(Dictionary<string, string> answers) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answers.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
