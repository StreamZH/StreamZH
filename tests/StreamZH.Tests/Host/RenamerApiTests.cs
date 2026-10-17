// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamZH.Tests.Host;

public sealed class RenamerApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new(new FakeTmdbClient());
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-renamer-").FullName;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Preview_apply_history_and_undo_through_the_api()
    {
        var client = await _factory.CreateOwnerClientAsync();
        Directory.CreateDirectory(Path.Combine(_media, "Breaking Bad", "Season 1"));
        File.WriteAllText(Path.Combine(_media, "Breaking Bad", "Season 1", "breaking.bad.s01e03.mkv"), "x");
        File.WriteAllText(Path.Combine(_media, "Breaking Bad", "Season 1", "breaking.bad.s01e03.en.srt"), "x");
        var folder = JsonValue.Create(_media)!.ToJsonString();

        var scan = (await (await client.PostAsync("/api/v1/renamer/scan", Json($$$"""{"source":{"path":{{{folder}}},"subfolders":true},"filter":{}}"""))).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(1, scan["matched"]!.GetValue<int>());

        // "type" after the other fields, enums in camelCase.
        var request = $$$"""
            {"source":{"path":{{{folder}}},"subfolders":true,"depth":3},"filter":{"extensions":"video"},
             "rules":[{"episodeFormat":"{Series Title} - {season:00}x{episode:00} - {Episode Title}","type":"media","match":"tmdb"}],
             "options":{"companions":true}}
            """;
        var preview = (await (await client.PostAsync("/api/v1/renamer/preview", Json(request))).Content.ReadFromJsonAsync<JsonObject>())!;
        var rows = preview["rows"]!.AsArray();
        Assert.Equal("Breaking Bad - 01x03 - Episode 3.mkv", rows[0]!["newName"]!.GetValue<string>());
        Assert.Equal("rename", rows[0]!["status"]!.GetValue<string>());
        Assert.Equal("Breaking Bad - 01x03 - Episode 3.en.srt", rows[1]!["newName"]!.GetValue<string>());
        Assert.Equal("tmdb", rows[0]!["media"]!["source"]!.GetValue<string>());

        var apply = await client.PostAsync("/api/v1/renamer/apply", Json(request));
        Assert.Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var runId = (await apply.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<int>();
        var run = await WaitAsync(client, runId, "completed");
        Assert.Equal(2, run["run"]!["done"]!.GetValue<int>());
        Assert.Equal("media", run["run"]!["rules"]![0]!["type"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_media, "Breaking Bad", "Season 1", "Breaking Bad - 01x03 - Episode 3.mkv")));

        var history = (await client.GetFromJsonAsync<JsonArray>("/api/v1/renamer/history"))!;
        Assert.Equal(runId, history[0]!["id"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/renamer/undo/{runId}", null)).StatusCode);
        await WaitAsync(client, runId, "undone");
        Assert.True(File.Exists(Path.Combine(_media, "Breaking Bad", "Season 1", "breaking.bad.s01e03.mkv")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/renamer/undo/{runId}", null)).StatusCode);
    }

    [Fact]
    public async Task Errors_presets_and_the_regex_tester()
    {
        var client = await _factory.CreateOwnerClientAsync();
        var folder = JsonValue.Create(_media)!.ToJsonString();

        var bad = await client.PostAsync("/api/v1/renamer/preview", Json("""{"source":{"path":"/"},"rules":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.NotNull((await bad.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!["source.path"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/renamer/preview", Json("""{"rules":[{"type":"nope"}]}"""))).StatusCode);

        var nothing = await client.PostAsync("/api/v1/renamer/apply", Json($$$"""{"source":{"path":{{{folder}}}},"rules":[]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        var invalidRule = await client.PostAsync("/api/v1/renamer/apply", Json($$$"""{"source":{"path":{{{folder}}}},"rules":[{"type":"replace","find":"(","useRegex":true}]}"""));
        Assert.NotNull((await invalidRule.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!["rules[0]"]);

        var saved = (await (await client.PostAsync("/api/v1/renamer/presets", Json("""{"name":"Clean","content":{"rules":[{"type":"remove","mode":"brackets"}]}}"""))).Content.ReadFromJsonAsync<JsonObject>())!;
        var id = saved["id"]!.GetValue<int>();
        Assert.Equal("brackets", saved["content"]!["rules"]![0]!["mode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/v1/renamer/presets/{id}", Json("""{"name":"Clean up","content":{}}"""))).StatusCode);
        Assert.Equal("Clean up", (await client.GetFromJsonAsync<JsonArray>("/api/v1/renamer/presets"))![0]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/renamer/presets/{id}")).StatusCode);

        var test = (await (await client.PostAsJsonAsync("/api/v1/renamer/regex-test", new { pattern = @"S(?<season>\d+)E(\d+)", input = "Show.S01E02.mkv", replacement = "${season}x$1", caseSensitive = false })).Content.ReadFromJsonAsync<JsonObject>())!;
        var match = test["matches"]![0]!;
        Assert.Equal("S01E02", match["value"]!.GetValue<string>());
        Assert.Equal("01", match["groups"]!.AsArray().Single(g => g!["name"]!.GetValue<string>() == "season")!["value"]!.GetValue<string>());

        // .NET numbers unnamed groups first: (\d+) is $1, the named group $2.
        Assert.Equal([1, 2], match["groups"]!.AsArray().Select(g => g!["number"]!.GetValue<int>()));
        Assert.Equal("Show.01x02.mkv", test["result"]!.GetValue<string>());
        var broken = (await (await client.PostAsJsonAsync("/api/v1/renamer/regex-test", new { pattern = "[a", input = "x" })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("A [ is not closed (at character 2).", broken["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_renamer_needs_an_admin()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/renamer/preview", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/renamer/history")).StatusCode);
    }

    private static async Task<JsonObject> WaitAsync(HttpClient client, int runId, string state)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var run = (await client.GetFromJsonAsync<JsonObject>($"/api/v1/renamer/history/{runId}"))!;
            if (run["run"]!["state"]!.GetValue<string>() == state || DateTime.UtcNow > until)
            {
                Assert.Equal(state, run["run"]!["state"]!.GetValue<string>());
                return run;
            }

            await Task.Delay(50);
        }
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
