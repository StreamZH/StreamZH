// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Metadata;

public class TmdbDetailsTests
{
    [Fact]
    public async Task Movie_details_with_certification_of_the_country_credits_and_collection()
    {
        var handler = new StubHandler("""
            {"id":438631,"title":"Dune","original_title":"Dune","release_date":"2021-09-15","runtime":155,"tagline":"Beyond fear, destiny awaits.",
             "vote_average":7.78,"vote_count":12000,"imdb_id":"tt1160419","genres":[{"name":"Science Fiction"}],
             "production_companies":[{"name":"Legendary Pictures"}],"production_countries":[{"iso_3166_1":"US","name":"United States of America"}],
             "belongs_to_collection":{"name":"Dune Collection"},
             "credits":{"cast":[{"name":"Rebecca Ferguson","character":"Jessica","order":1},{"name":"Timothée Chalamet","character":"Paul Atreides","order":0,"profile_path":"/tc.jpg"}],
                        "crew":[{"name":"Denis Villeneuve","job":"Director"},{"name":"Jon Spaihts","job":"Screenplay"},{"name":"Denis Villeneuve","job":"Screenplay"},{"name":"Hans Zimmer","job":"Original Music Composer"}]},
             "release_dates":{"results":[
                {"iso_3166_1":"US","release_dates":[{"certification":"PG-13","type":3}]},
                {"iso_3166_1":"DE","release_dates":[{"certification":"","type":1},{"certification":"16","type":5},{"certification":"12","type":3}]}]}}
            """);
        var client = new TmdbClient(new HttpClient(handler), new Settings());

        var details = await client.GetMovieDetailsAsync(438631, "DE");

        Assert.Equal("12", details.Certification);
        Assert.Contains("append_to_response=credits,release_dates", handler.Last!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(7.78, details.Rating);
        Assert.Equal(12000, details.Votes);
        Assert.Equal("Beyond fear, destiny awaits.", details.Tagline);
        Assert.Equal("Dune Collection", details.Collection);
        Assert.Equal(["Legendary Pictures"], details.Studios);
        Assert.Equal(["United States of America"], details.Countries);
        Assert.Equal(["Timothée Chalamet", "Rebecca Ferguson"], details.Cast.Select(c => c.Name));
        Assert.Equal(["Denis Villeneuve"], details.Directors);
        Assert.Equal(["Jon Spaihts", "Denis Villeneuve"], details.Writers);
        Assert.Equal("Dune", details.Movie.Title);
        Assert.Null((await client.GetMovieDetailsAsync(438631, "FR")).Certification);
    }

    [Fact]
    public async Task Series_and_season_details()
    {
        var handler = new StubHandler("""
            {"id":1396,"name":"Breaking Bad","first_air_date":"2008-01-20","vote_average":8.9,"vote_count":15000,"networks":[{"name":"AMC"}],
             "seasons":[{"season_number":1,"name":"Season 1","poster_path":"/s1.jpg"},{"season_number":0,"name":"Specials","poster_path":null}],
             "external_ids":{"tvdb_id":81189},"content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]},"credits":{"cast":[]}}
            """);
        var client = new TmdbClient(new HttpClient(handler), new Settings());

        var series = await client.GetSeriesDetailsAsync(1396, "us");

        Assert.Equal("TV-MA", series.Certification);
        Assert.Equal(81189, series.Series.TvdbId);
        Assert.Equal([(0, (string?)null), (1, "/s1.jpg")], series.Seasons.Select(s => (s.SeasonNumber, s.PosterPath)));

        handler.Body = """
            {"name":"Season 1","poster_path":"/s1.jpg","episodes":[
              {"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2008-01-20","still_path":"/p.jpg","vote_average":8.2,"vote_count":10,"runtime":58,
               "crew":[{"name":"Vince Gilligan","job":"Director"},{"name":"Vince Gilligan","job":"Writer"}]}]}
            """;
        var season = await client.GetSeasonDetailsAsync(1396, 1);
        var pilot = Assert.Single(season.Episodes);
        Assert.Equal(("Pilot", "/p.jpg", 58, 8.2), (pilot.Title, pilot.StillPath, pilot.Runtime!.Value, pilot.Rating!.Value));
        Assert.Equal(["Vince Gilligan"], pilot.Directors);
        Assert.Equal(["Vince Gilligan"], pilot.Writers);
    }

    [Fact]
    public async Task Image_client_downloads_and_rejects_bad_paths()
    {
        var handler = new StubHandler("PNGDATA");
        var images = new TmdbImageClient(new HttpClient(handler) { BaseAddress = TmdbImages.BaseAddress });
        using var target = new MemoryStream();

        await images.DownloadAsync("/abc_DEF-1.png", "w342", target);

        Assert.Equal("https://image.tmdb.org/t/p/w342/abc_DEF-1.png", handler.Last!.RequestUri!.ToString());
        Assert.Equal("PNGDATA", Encoding.UTF8.GetString(target.ToArray()));
        await Assert.ThrowsAsync<TmdbException>(() => images.DownloadAsync("/../x.jpg", "w342", Stream.Null));
        await Assert.ThrowsAsync<TmdbException>(() => images.DownloadAsync("/x.exe", "w342", Stream.Null));
        await Assert.ThrowsAsync<TmdbException>(() => images.DownloadAsync("/x.jpg", "w9999", Stream.Null));

        handler.Status = HttpStatusCode.NotFound;
        var missing = await Assert.ThrowsAsync<TmdbException>(() => images.DownloadAsync("/gone.jpg", "original", Stream.Null));
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal("https://image.tmdb.org/t/p/w500/x.jpg", TmdbImages.Url("/x.jpg", "w500"));
        Assert.Null(TmdbImages.Url(null));
    }

    private sealed record Settings(string? Token = "eyJhbGciOiJIUzI1NiJ9.long-read-access-token", string Language = "en-US") : ITmdbSettings;

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
