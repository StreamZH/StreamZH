// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Library;

namespace StreamZH.Tests.Host;

/// <summary>Wanted, Calendar and the iCal feed over HTTP (FakeTmdbClient: Dune, Arrival and Breaking Bad with 21 aired episodes).</summary>
public sealed class WantedApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-wanted-").FullName;

    private async Task<(HttpClient Client, int DuneId, int SeriesId)> SetupAsync()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var movies = await AddRootFolderAsync(client, "movies", "Movie");
        var series = await AddRootFolderAsync(client, "series", "Series");
        var dune = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = movies });
        var arrival = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 329865, rootFolderId = movies });
        var show = await client.PostAsJsonAsync("/api/v1/series", new { tmdbId = 1396, rootFolderId = series });
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created), (dune.StatusCode, arrival.StatusCode, show.StatusCode));
        var duneId = (await dune.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var seriesId = (await show.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        await WaitForJobsAsync(client);
        return (client, duneId, seriesId);
    }

    /// <summary>Adding a series loads its episodes in a background job.</summary>
    private static async Task WaitForJobsAsync(HttpClient client)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await client.GetFromJsonAsync<StreamZH.Core.Jobs.JobStatus>("/api/v1/system/jobs");
            if (status!.Queued == 0 && status.Running is null)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Background jobs did not finish.");
    }

    [Fact]
    public async Task Missing_lists_are_paged_sorted_and_filtered()
    {
        var (client, duneId, _) = await SetupAsync();

        var movies = await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/missing?type=movies&sortKey=title&sortDirection=ascending");
        Assert.Equal(2, movies.GetProperty("totalRecords").GetInt32());
        Assert.Equal(["Arrival", "Dune"], movies.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        Assert.Equal((2, 20), (movies.GetProperty("counts").GetProperty("movies").GetInt32(), movies.GetProperty("counts").GetProperty("episodes").GetInt32()));
        Assert.Equal(("title", "ascending"), (movies.GetProperty("sortKey").GetString(), movies.GetProperty("sortDirection").GetString()));

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/missing?type=episodes&page=2&pageSize=7&sortKey=title&sortDirection=asc");
        Assert.Equal((2, 7, 20), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalRecords").GetInt32()));
        var first = page.GetProperty("records")[0];
        Assert.Equal(("episode", "Breaking Bad", 2, 1, "missing"), (
            first.GetProperty("type").GetString(), first.GetProperty("title").GetString(), first.GetProperty("seasonNumber").GetInt32(),
            first.GetProperty("episodeNumber").GetInt32(), first.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("lastSearched").ValueKind);

        // Unmonitor selected: Dune and the first episode leave the list.
        var episodeId = first.GetProperty("id").GetInt32();
        var unmonitor = await client.PostAsJsonAsync("/api/v1/wanted/monitor", new { movieIds = new[] { duneId }, episodeIds = new[] { episodeId }, monitored = false });
        Assert.Equal(2, (await unmonitor.Content.ReadFromJsonAsync<WantedMonitorResponse>())!.Changed);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/missing?type=movies");
        Assert.Equal(1, after.GetProperty("totalRecords").GetInt32());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/missing?type=movies&monitored=false")).GetProperty("totalRecords").GetInt32());

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/summary");
        Assert.Equal((1, 19, 0), (summary.GetProperty("missingMovies").GetInt32(), summary.GetProperty("missingEpisodes").GetInt32(), summary.GetProperty("cutoffUnmetMovies").GetInt32()));
        var cutoff = await client.GetFromJsonAsync<JsonElement>("/api/v1/wanted/cutoff?type=episodes");
        Assert.Equal(0, cutoff.GetProperty("totalRecords").GetInt32());
    }

    [Fact]
    public async Task Bad_list_parameters_are_field_errors()
    {
        var client = await _factory.CreateOwnerClientAsync();
        foreach (var (query, field) in new[]
        {
            ("type=music", "type"), ("pageSize=0", "pageSize"), ("pageSize=251", "pageSize"), ("page=abc", "page"),
            ("sortKey=size", "sortKey"), ("sortDirection=up", "sortDirection"), ("monitored=maybe", "monitored"), ("available=2", "available"),
        })
        {
            var response = await client.GetAsync($"/api/v1/wanted/cutoff?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _), query);
        }
    }

    [Fact]
    public async Task Search_buttons_queue_jobs_and_need_an_admin()
    {
        var (client, duneId, _) = await SetupAsync();

        var selected = await client.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "missing", movieIds = new[] { duneId } });
        Assert.Equal(HttpStatusCode.Accepted, selected.StatusCode);
        Assert.Equal(1, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());

        var all = await client.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "cutoff", all = true, type = "episodes" });
        Assert.Equal(HttpStatusCode.Accepted, all.StatusCode);
        Assert.Equal(1, (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "everything", all = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "missing", all = true, type = "books" })).StatusCode);

        var users = _factory.Services.GetRequiredService<UserRepository>();
        users.Create("friend", _factory.Services.GetRequiredService<PasswordService>().Hash("friend password 1"), StreamZH.Core.Users.UserRole.Friend);
        var friend = _factory.CreateClient();
        await friend.PostAsJsonAsync("/api/v1/auth/login", new { username = "friend", password = "friend password 1", rememberMe = false });
        Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/api/v1/wanted/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/wanted/search", new { kind = "missing", all = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await friend.PostAsJsonAsync("/api/v1/wanted/monitor", new { movieIds = new[] { duneId } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/wanted/missing")).StatusCode);
    }

    [Fact]
    public async Task Calendar_returns_days_in_the_range_and_reads_offsets_as_local_days()
    {
        var (client, duneId, _) = await SetupAsync();
        var movies = _factory.Services.GetRequiredService<MovieRepository>();
        var dune = movies.Find(duneId)!;
        dune.InCinemas = "2026-10-01";
        dune.DigitalRelease = "2026-10-31";
        movies.Update(dune);

        var october = await client.GetFromJsonAsync<JsonElement>("/api/v1/calendar?start=2026-10-01&end=2026-10-31");
        Assert.Equal(["inCinemas", "digital"], october.EnumerateArray().Select(e => e.GetProperty("releaseType").GetString()));
        Assert.Equal(("Dune", "2026-10-01", false), (october[0].GetProperty("title").GetString(), october[0].GetProperty("date").GetString(), october[0].GetProperty("hasFile").GetBoolean()));

        // Midnight in Zurich (UTC+2) is still October 1st there, although it is September 30th in UTC.
        var zurich = await client.GetFromJsonAsync<JsonElement>("/api/v1/calendar?start=2026-10-01T00:00:00%2B02:00&end=2026-10-01T23:59:59%2B02:00");
        Assert.Equal(["2026-10-01"], zurich.EnumerateArray().Select(e => e.GetProperty("date").GetString()));
        var utc = await client.GetFromJsonAsync<JsonElement>("/api/v1/calendar?start=2026-09-30T22:00:00Z&end=2026-09-30T23:00:00Z");
        Assert.Empty(utc.EnumerateArray());

        // The old episodes of 2008 are found too; "type" filters.
        var episodes = await client.GetFromJsonAsync<JsonElement>("/api/v1/calendar?start=2008-01-20&end=2008-01-26&type=series");
        Assert.Equal(7, episodes.GetArrayLength());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/calendar?start=2008-01-20&end=2008-01-26&type=movies")).GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?start=2026-10-31&end=2026-10-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?start=2026-01-01&end=2027-12-31")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?start=yesterday")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?type=books")).StatusCode);
    }

    [Fact]
    public async Task The_ical_feed_signs_in_with_its_own_read_only_token()
    {
        var (owner, duneId, _) = await SetupAsync();
        var token = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/calendar/feed-token")).GetProperty("token").GetString();
        var movies = _factory.Services.GetRequiredService<MovieRepository>();
        var dune = movies.Find(duneId)!;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        dune.InCinemas = today.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        dune.PhysicalRelease = today.AddDays(60).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        movies.Update(dune);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/calendar/feed.ics")).StatusCode);

        // 0.12 (security review S1-04): the API key in the address no longer opens the feed (nor anything else).
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?apikey={_factory.ApiKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/system/status?apikey={_factory.ApiKey}")).StatusCode);

        var response = await anonymous.GetAsync($"/api/v1/calendar/feed.ics?token={token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Dune (In cinemas)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Physical release", text, StringComparison.Ordinal);

        var longer = await anonymous.GetStringAsync($"/api/v1/calendar/feed.ics?token={token}&futureDays=90&tags=movies");
        Assert.Contains("SUMMARY:Dune (Physical release)", longer, StringComparison.Ordinal);
        var seriesOnly = await anonymous.GetStringAsync($"/api/v1/calendar/feed.ics?token={token}&futureDays=90&tags=series");
        Assert.DoesNotContain("Dune", seriesOnly, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?token={token}&pastDays=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?token={token}&tags=books")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/calendar/feed.ics?token=wrong")).StatusCode);

        // The token opens the feed only, not the API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/calendar?token={token}")).StatusCode);
        var withTokenHeader = _factory.CreateClient();
        withTokenHeader.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withTokenHeader.GetAsync("/api/v1/system/status")).StatusCode);

        // Members signed in (or with the API key header) read it without the token.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/calendar/feed.ics")).StatusCode);

        // A new token ends the old links.
        var renewed = await owner.PostAsync("/api/v1/calendar/feed-token", null);
        var newToken = (await renewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.NotEqual(token, newToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?token={newToken}")).StatusCode);
    }

    [Fact]
    public async Task First_day_of_week_is_a_ui_setting()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var defaults = (await client.GetFromJsonAsync<UiSettingsResource>("/api/v1/settings/ui"))!;
        Assert.Equal("monday", defaults.FirstDayOfWeek);
        Assert.Equal(["monday", "sunday"], defaults.FirstDaysOfWeek);

        var invalid = await client.PutAsJsonAsync("/api/v1/settings/ui", new { firstDayOfWeek = "friday" });
        Assert.True((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("firstDayOfWeek", out _));

        var saved = (await (await client.PutAsJsonAsync("/api/v1/settings/ui", new { firstDayOfWeek = "sunday" })).Content.ReadFromJsonAsync<UiSettingsResource>())!;
        Assert.Equal("sunday", saved.FirstDayOfWeek);
        Assert.Equal("locale", saved.ShortDateFormat);
    }

    private async Task<int> AddRootFolderAsync(HttpClient client, string name, string mediaType)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, name)).FullName;
        var response = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_media, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
