// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Host.Api;
using StreamZH.Library;
using StreamZH.Tests.CustomFormats;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

/// <summary>Settings → Custom Formats API, profile scores, and custom formats on the release pages.</summary>
public sealed class CustomFormatApiTests : IDisposable
{
    private const int DeutschHd = 4;

    private readonly StreamZhFactory _base = new(new FakeTmdbClient());
    private readonly FakeIndexerProvider _indexers = new();
    private readonly FakeIndexerClient _client = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-media-").FullName;

    public CustomFormatApiTests()
    {
        _factory = _base.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IIndexerProvider>(_indexers);
            services.AddSingleton<IIndexerClient>(_client);
        }));
        _indexers.Indexers.Add(FakeIndexerProvider.Definition(1, "Alpha"));
    }

    private static object Spec(string name, string implementation, object fields, bool negate = false, bool required = false) =>
        new { name, implementation, negate, required, fields };

    [Fact]
    public async Task Creates_validates_updates_and_deletes_formats()
    {
        var client = await OwnerClientAsync();
        Assert.Empty((await client.GetFromJsonAsync<CustomFormatResource[]>("/api/v1/customformat"))!);

        var invalid = await client.PostAsJsonAsync("/api/v1/customformat", new
        {
            name = " ",
            mediaType = "movie",
            specifications = new[]
            {
                Spec("Bad regex", "ReleaseTitleSpecification", new { value = "([" }),
                Spec("Size", "SizeSpecification", new { min = 5, max = 2 }),
                Spec("What", "NopeSpecification", new { value = 1 }),
                Spec("Lang", "LanguageSpecification", new { value = 999 }),
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(
            ["name", "specifications[0].fields.value", "specifications[1].fields.max", "specifications[2].implementation", "specifications[3].fields.value"],
            errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("This is not a valid regular expression.", errors.GetProperty("specifications[0].fields.value")[0].GetString());

        var noConditions = await client.PostAsJsonAsync("/api/v1/customformat", new { name = "Empty", mediaType = "movie", specifications = Array.Empty<object>() });
        Assert.Equal("Add at least one condition.", (await noConditions.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("specifications")[0].GetString());

        var created = await client.PostAsJsonAsync("/api/v1/customformat", new
        {
            name = "x265",
            mediaType = "movie",
            includeInRename = true,
            specifications = new[]
            {
                Spec("x265", "releasetitle", new { value = @"[xh][ ._-]?265" }, required: true),
                Spec("Not 2160p", "ResolutionSpecification", new { value = "2160" }, negate: true, required: true),
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var format = (await created.Content.ReadFromJsonAsync<CustomFormatResource>())!;
        Assert.Equal(("x265", "movie", true, 0, (string?)null), (format.Name, format.MediaType, format.IncludeInRename, format.UsedBy, format.TrashId));
        Assert.Equal(["ReleaseTitleSpecification", "ResolutionSpecification"], format.Specifications.Select(s => s.Implementation));
        Assert.Equal(JsonValueKind.Number, format.Specifications[1].Fields["value"].ValueKind);

        // Same name for movies is taken; for series it is free.
        var duplicate = await client.PostAsJsonAsync("/api/v1/customformat", new
        {
            name = "X265", mediaType = "movie", specifications = new[] { Spec("x", "ReleaseTitleSpecification", new { value = "x" }) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var series = await client.PostAsJsonAsync("/api/v1/customformat", new
        {
            name = "x265", mediaType = "series", specifications = new[] { Spec("Season pack", "ReleaseTypeSpecification", new { value = 3 }) },
        });
        Assert.Equal(HttpStatusCode.Created, series.StatusCode);

        var updated = await client.PutAsJsonAsync($"/api/v1/customformat/{format.Id}", new
        {
            name = "x265 (HD)",
            includeInRename = false,
            specifications = format.Specifications,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("x265 (HD)", (await updated.Content.ReadFromJsonAsync<CustomFormatResource>())!.Name);
        Assert.Equal(2, (await client.GetFromJsonAsync<CustomFormatResource[]>("/api/v1/customformat"))!.Length);
        Assert.Single((await client.GetFromJsonAsync<CustomFormatResource[]>("/api/v1/customformat?mediaType=series"))!);

        // A score in a profile counts as "used"; deleting the format removes the score.
        var profile = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        var saved = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", profile with
        {
            FormatItems = [.. profile.FormatItems.Select(i => i.CustomFormatId == format.Id ? i with { Score = -10000 } : i)],
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<CustomFormatResource>($"/api/v1/customformat/{format.Id}"))!.UsedBy);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/customformat/{format.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/customformat/{format.Id}")).StatusCode);
        var after = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        Assert.DoesNotContain(after.FormatItems, i => i.CustomFormatId == format.Id);
        var stored = _factory.Services.GetRequiredService<StreamZH.Data.QualityProfileRepository>().Find(DeutschHd)!;
        Assert.Empty(stored.FormatItems);
    }

    [Fact]
    public async Task Schema_lists_the_conditions_and_choices_per_media_type()
    {
        var client = await OwnerClientAsync();

        var movie = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/customformat/schema?mediaType=movie"))!;
        var series = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/customformat/schema?mediaType=series"))!;
        static string[] Names(JsonElement[] list) => [.. list.Select(i => i.GetProperty("implementation").GetString()!)];

        Assert.Contains("EditionSpecification", Names(movie));
        Assert.Contains("QualityModifierSpecification", Names(movie));
        Assert.Contains("YearSpecification", Names(movie));
        Assert.DoesNotContain("ReleaseTypeSpecification", Names(movie));
        Assert.Contains("ReleaseTypeSpecification", Names(series));
        Assert.DoesNotContain("EditionSpecification", Names(series));

        static JsonElement Field(JsonElement[] list, string implementation, string field) => list
            .Single(i => i.GetProperty("implementation").GetString() == implementation)
            .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == field);

        var movieSource = Field(movie, "SourceSpecification", "value");
        Assert.Equal("select", movieSource.GetProperty("type").GetString());
        Assert.Contains(movieSource.GetProperty("options").EnumerateArray(), o => o.GetProperty("value").GetInt32() == 9 && o.GetProperty("label").GetString() == "BLURAY");
        Assert.Contains(Field(series, "SourceSpecification", "value").GetProperty("options").EnumerateArray(), o => o.GetProperty("value").GetInt32() == 7 && o.GetProperty("label").GetString() == "Bluray Raw");
        Assert.Equal("GB", Field(movie, "SizeSpecification", "min").GetProperty("unit").GetString());
        Assert.Equal("regex", Field(movie, "ReleaseTitleSpecification", "value").GetProperty("type").GetString());
        Assert.Equal("flags", Field(movie, "IndexerFlagSpecification", "value").GetProperty("type").GetString());
        Assert.All(movie.Concat(series).SelectMany(i => i.GetProperty("fields").EnumerateArray()), f => Assert.Contains(f.GetProperty("name").GetString(), new[] { "value", "min", "max" }));
        Assert.All(movie.Concat(series).SelectMany(i => i.GetProperty("fields").EnumerateArray()),
            f => Assert.Contains(f.GetProperty("type").GetString(), new[] { "text", "regex", "number", "select", "flags" }));
        Assert.Contains(Field(series, "LanguageSpecification", "value").GetProperty("options").EnumerateArray(), o => o.GetProperty("value").GetInt32() == 26 && o.GetProperty("label").GetString() == "Arabic");
    }

    [Fact]
    public async Task Imports_TRaSH_and_Radarr_JSON_and_exports_it_again()
    {
        var client = await OwnerClientAsync();
        var array = "[" + File.ReadAllText(CustomFormatTestData.FixturePath("radarr", "german-dl.json")) + ","
            + File.ReadAllText(CustomFormatTestData.FixturePath("radarr", "x265-hd.json")) + "]";

        var imported = await client.PostAsJsonAsync("/api/v1/customformat/import", new { mediaType = "movie", json = array });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var formats = (await imported.Content.ReadFromJsonAsync<CustomFormatResource[]>())!;
        Assert.Equal(["German DL", "x265 (HD)"], formats.Select(f => f.Name));
        Assert.Equal("f845be10da4f442654c13e1f2c3d6cd5", formats[0].TrashId);
        Assert.True(formats[0].IncludeInRename);

        // Again: the same formats are updated (matched by TRaSH id), not added.
        var again = (await (await client.PostAsJsonAsync("/api/v1/customformat/import", new { mediaType = "movie", json = array })).Content.ReadFromJsonAsync<CustomFormatResource[]>())!;
        Assert.Equal(formats.Select(f => f.Id), again.Select(f => f.Id));
        Assert.Equal(2, (await client.GetFromJsonAsync<CustomFormatResource[]>("/api/v1/customformat"))!.Length);

        // Radarr's API shape (fields as a list) as a JSON object instead of a string.
        var radarr = JsonSerializer.Deserialize<JsonElement>(
            """
            { "id": 7, "name": "Remux", "includeCustomFormatWhenRenaming": false, "specifications": [
              { "name": "Remux", "implementation": "QualityModifierSpecification", "implementationName": "Quality Modifier", "negate": false, "required": true,
                "fields": [ { "order": 0, "name": "value", "label": "Quality Modifier", "value": 5, "type": "select" } ] } ] }
            """);
        var remux = (await (await client.PostAsJsonAsync("/api/v1/customformat/import", new { mediaType = "movie", json = radarr })).Content.ReadFromJsonAsync<CustomFormatResource[]>())!;
        Assert.Equal(5, Assert.Single(remux).Specifications[0].Fields["value"].GetInt32());

        // Export gives the Radarr/TRaSH shape back.
        var export = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customformat/{formats[0].Id}/export");
        Assert.Equal("German DL", export.GetProperty("name").GetString());
        Assert.Equal("f845be10da4f442654c13e1f2c3d6cd5", export.GetProperty("trash_id").GetString());
        Assert.True(export.GetProperty("includeCustomFormatWhenRenaming").GetBoolean());
        Assert.Equal(-2, export.GetProperty("specifications")[1].GetProperty("fields").GetProperty("value").GetInt32());

        // Broken input names the problem; nothing is saved.
        var broken = await client.PostAsJsonAsync("/api/v1/customformat/import", new { mediaType = "movie", json = "{ nope" });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.True((await broken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("json", out _));
        var invalidSecond = await client.PostAsJsonAsync("/api/v1/customformat/import", new
        {
            mediaType = "series",
            json = """[{ "name": "Ok", "specifications": [{ "name": "a", "implementation": "ReleaseTitleSpecification", "fields": { "value": "a" } }] }, { "name": "", "specifications": [] }]""",
        });
        var importErrors = (await invalidSecond.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(importErrors.TryGetProperty("[1].name", out _));
        Assert.Empty((await client.GetFromJsonAsync<CustomFormatResource[]>("/api/v1/customformat?mediaType=series"))!);
    }

    [Fact]
    public async Task Test_endpoint_shows_every_format_its_conditions_and_the_score()
    {
        var client = await OwnerClientAsync();
        await ImportTrashAsync(client, "german-dl.json", "x265-hd.json", "german-bluray-tier-01.json");
        await SetScoresAsync(client, ("German DL", 11000), ("German Bluray Tier 01", 2900), ("x265 (HD)", -10000));

        var response = await client.PostAsJsonAsync("/api/v1/customformat/test", new
        {
            title = "Dune.Part.Two.2024.German.DL.1080p.BluRay.x264-ZeroTwo",
            mediaType = "movie",
            sizeGb = 12.5,
            qualityProfileId = DeutschHd,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(13900, result.GetProperty("totalScore").GetInt32());
        var parsed = result.GetProperty("parsed");
        Assert.Equal(("Bluray-1080p", "BLURAY", 1080, "ZeroTwo"), (parsed.GetProperty("quality").GetString(), parsed.GetProperty("source").GetString(),
            parsed.GetProperty("resolution").GetInt32(), parsed.GetProperty("releaseGroup").GetString()));
        Assert.Equal(["German", "English"], parsed.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));

        var formats = result.GetProperty("formats").EnumerateArray().ToList();
        Assert.Equal(
            [("German Bluray Tier 01", true, 2900), ("German DL", true, 11000), ("x265 (HD)", false, -10000)],
            formats.Select(f => (f.GetProperty("name").GetString(), f.GetProperty("matched").GetBoolean(), f.GetProperty("score").GetInt32())));
        var x265 = formats[2].GetProperty("specifications").EnumerateArray().ToList();
        Assert.Equal([false, true], x265.Select(s => s.GetProperty("matched").GetBoolean()));

        var noProfile = await (await client.PostAsJsonAsync("/api/v1/customformat/test", new { title = "Show.S01E01.1080p.WEB.x265-GRP", mediaType = "series" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Single Episode", noProfile.GetProperty("parsed").GetProperty("releaseType").GetString());
        Assert.Empty(noProfile.GetProperty("formats").EnumerateArray());

        var invalid = await client.PostAsJsonAsync("/api/v1/customformat/test", new { title = "", mediaType = "music", qualityProfileId = 999 });
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(["mediaType", "qualityProfileId", "title"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Profiles_round_trip_scores_groups_and_validate_them()
    {
        var client = await OwnerClientAsync();
        await ImportTrashAsync(client, "german-dl.json", "x265-hd.json");

        var profile = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        Assert.Equal([("German DL", "movie", 0), ("x265 (HD)", "movie", 0)], profile.FormatItems.Select(i => (i.Name, i.MediaType, i.Score)));
        Assert.Equal((0, 0, 1), (profile.MinFormatScore, profile.CutoffFormatScore, profile.MinUpgradeFormatScore));

        var items = profile.Items.ToList();
        var web = items.FindIndex(i => i.Name == "WEBDL-1080p");
        var webRip = items.FindIndex(i => i.Name == "WEBRip-1080p");
        Assert.Equal(web + 1, webRip);
        items[web] = items[web] with { Group = "WEB 1080p" };
        items[webRip] = items[webRip] with { Group = "WEB 1080p" };

        var changed = profile with
        {
            Items = items,
            FormatItems = [profile.FormatItems[0] with { Score = 11000 }, profile.FormatItems[1] with { Score = -10000 }],
            MinFormatScore = 100,
            CutoffFormatScore = 12000,
            MinUpgradeFormatScore = 10,
            TrashMovieTemplate = "abc",
        };
        var saved = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", changed);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var back = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        Assert.Equal([11000, -10000], back.FormatItems.Select(i => i.Score));
        Assert.Equal((100, 12000, 10, "abc"), (back.MinFormatScore, back.CutoffFormatScore, back.MinUpgradeFormatScore, back.TrashMovieTemplate));
        Assert.Equal(["WEB 1080p", "WEB 1080p"], back.Items.Where(i => i.Group is not null).Select(i => i.Group));

        // A client that does not know about formats keeps them.
        var old = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", new
        {
            name = back.Name, items = back.Items, cutoffQualityId = back.CutoffQualityId, languageId = back.LanguageId, upgradeAllowed = true,
            trashMovieTemplate = "abc",
        });
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var kept = (await old.Content.ReadFromJsonAsync<QualityProfileResource>())!;
        Assert.Equal([11000, -10000], kept.FormatItems.Select(i => i.Score));
        Assert.Equal(12000, kept.CutoffFormatScore);

        var invalid = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", back with
        {
            FormatItems = [new ProfileFormatItemResource(999, "?", "movie", 5), back.FormatItems[1] with { Score = 2_000_000 }],
            MinUpgradeFormatScore = 0,
            Items = [.. back.Items.Select((i, n) => n == 0 ? i with { Group = "WEB 1080p" } : i)],
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("formatItems[0].customFormatId", out _));
        Assert.True(errors.TryGetProperty("formatItems[1].score", out _));
        Assert.True(errors.TryGetProperty("minUpgradeFormatScore", out _));
        Assert.Contains(errors.EnumerateObject(), p => p.Name.StartsWith("items[", StringComparison.Ordinal) && p.Name.EndsWith("].group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Release_test_page_and_interactive_search_show_formats_and_scores()
    {
        var client = await OwnerClientAsync();
        await ImportTrashAsync(client, "german-dl.json", "x265-hd.json");
        await SetScoresAsync(client, ("German DL", 11000), ("x265 (HD)", -10000));
        var movie = await AddMovieAsync(client);

        var parsed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/parse?title=Dune.2021.German.DL.1080p.WEB.x265-GRP&movieId={movie.Id}");
        Assert.Equal(["German DL", "x265 (HD)"], parsed.GetProperty("customFormats").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(1000, parsed.GetProperty("customFormatScore").GetInt32());
        Assert.Equal(1000, parsed.GetProperty("decision").GetProperty("customFormatScore").GetInt32());

        var noItem = await client.GetFromJsonAsync<JsonElement>("/api/v1/parse?title=Dune.2021.German.DL.1080p.WEB.x265-GRP");
        Assert.Equal(2, noItem.GetProperty("customFormats").GetArrayLength());
        Assert.Equal(0, noItem.GetProperty("customFormatScore").GetInt32());

        // Minimum score: x265 alone (-10000) is rejected.
        await SetScoresAsync(client, ("German DL", 11000), ("x265 (HD)", -10000));
        _client.Returns(1,
            Release("Dune.2021.German.DL.1080p.WEB.H264-GRP"),
            Release("Dune.2021.German.1080p.WEB.x265-GRP"),
            Release("Dune.2021.German.DL.1080p.WEB.x265-GRP"));
        var search = (await client.GetFromJsonAsync<ReleaseSearchResource>($"/api/v1/release?movieId={movie.Id}"))!;
        Assert.Equal(
            [("Dune.2021.German.DL.1080p.WEB.H264-GRP", 11000, true), ("Dune.2021.German.DL.1080p.WEB.x265-GRP", 1000, true), ("Dune.2021.German.1080p.WEB.x265-GRP", -10000, false)],
            search.Releases.Select(r => (r.Title, r.CustomFormatScore, r.Accepted)));
        Assert.Equal(["German DL"], search.Releases[0].CustomFormats.Select(f => f.Name));
        Assert.Contains(search.Releases[2].Rejections, r => r.Code == "CustomFormatMinimumScore" && r.Message.StartsWith("Custom format score -10000 is below the minimum 0", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task ImportTrashAsync(HttpClient client, params string[] files)
    {
        var json = "[" + string.Join(",", files.Select(f => File.ReadAllText(CustomFormatTestData.FixturePath("radarr", f)))) + "]";
        var response = await client.PostAsJsonAsync("/api/v1/customformat/import", new { mediaType = "movie", json });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SetScoresAsync(HttpClient client, params (string Name, int Score)[] scores)
    {
        var profile = (await client.GetFromJsonAsync<QualityProfileResource>($"/api/v1/qualityprofiles/{DeutschHd}"))!;
        var map = scores.ToDictionary(s => s.Name, s => s.Score);
        var response = await client.PutAsJsonAsync($"/api/v1/qualityprofiles/{DeutschHd}", profile with
        {
            FormatItems = [.. profile.FormatItems.Select(i => i with { Score = map.GetValueOrDefault(i.Name) })],
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static ReleaseInfo Release(string title) => new()
    {
        Guid = $"guid-{title}",
        Title = title,
        DownloadUrl = $"https://alpha.example/get/{title}",
        Size = 4L * 1024 * 1024 * 1024,
        PublishDate = DateTimeOffset.UtcNow.AddDays(-2),
        Grabs = 10,
    };

    private async Task<HttpClient> OwnerClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<MovieResource> AddMovieAsync(HttpClient client)
    {
        var path = Directory.CreateDirectory(Path.Combine(_media, "movies")).FullName;
        var root = await client.PostAsJsonAsync("/api/v1/rootfolders", new { path, mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var rootId = (await root.Content.ReadFromJsonAsync<RootFolderInfo>())!.Id;
        var response = await client.PostAsJsonAsync("/api/v1/movies", new { tmdbId = 438631, rootFolderId = rootId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieResource>())!;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _base.Dispose();
        Directory.Delete(_media, recursive: true);
    }
}
