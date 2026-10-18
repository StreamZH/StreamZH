// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Text;
using StreamZH.Core.Media;
using StreamZH.Library.Search;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Tests.Library.Wanted;

/// <summary>Digital and physical release dates from TMDB, for the calendar and the "Released" minimum availability.</summary>
public sealed class MovieReleaseDatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_earliest_digital_and_physical_release_of_any_country_is_used()
    {
        var handler = new StubHandler("""
            {"id":438631,"title":"Dune","original_title":"Dune","release_date":"2021-09-15","runtime":155,"status":"Released",
             "release_dates":{"results":[
                {"iso_3166_1":"US","release_dates":[{"type":3,"release_date":"2021-10-22T00:00:00.000Z"},{"type":4,"release_date":"2021-10-22T00:00:00.000Z"},{"type":5,"release_date":"2022-01-11T00:00:00.000Z"}]},
                {"iso_3166_1":"DE","release_dates":[{"type":4,"release_date":"2021-10-21T00:00:00.000Z"},{"type":5,"release_date":"bad"},{"type":5}]}]}}
            """);
        var client = new TmdbClient(new HttpClient(handler), new Settings());

        var movie = await client.GetMovieAsync(438631);

        Assert.Equal(("2021-09-15", "2021-10-21", "2022-01-11"), (movie.ReleaseDate, movie.DigitalRelease, movie.PhysicalRelease));
        Assert.Contains("append_to_response=release_dates", handler.Last!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_release_dates_both_are_unknown()
    {
        var client = new TmdbClient(new HttpClient(new StubHandler("""{"id":1,"title":"X","release_date":"2027-01-01"}""")), new Settings());
        var movie = await client.GetMovieAsync(1);
        Assert.Equal((null, null), (movie.DigitalRelease, movie.PhysicalRelease));
    }

    [Theory]
    [InlineData("2026-10-01", null, null, 0, "2026-12-30")]
    [InlineData("2026-10-01", "2026-10-15", null, 0, null)]
    [InlineData("2026-10-01", "2026-11-20", "2026-11-05", 0, "2026-11-05")]
    [InlineData("2026-10-01", "2026-10-15", null, 7, "2026-10-22")]
    [InlineData(null, null, "2026-10-01", 0, null)]
    public void Released_uses_the_home_release_when_known(string? cinema, string? digital, string? physical, int delay, string? expected)
    {
        var movie = new Movie { Title = "Test", Year = 2026, Status = "Post Production", InCinemas = cinema, DigitalRelease = digital, PhysicalRelease = physical };
        var from = IndexerOptions.AvailableFrom(movie, MinimumAvailability.Released, delay, Now);
        Assert.Equal(expected, from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // "In cinemas" is not affected by the home release.
        if (cinema is not null)
        {
            Assert.Null(IndexerOptions.AvailableFrom(movie, MinimumAvailability.InCinemas, 0, Now));
        }
    }

    private sealed class Settings : ITmdbSettings
    {
        public string? Token => "eyJhbGciOiJIUzI1NiJ9.long-read-access-token";

        public string Language => "en-US";
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
