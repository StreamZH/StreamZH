// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Migration;
using StreamZH.Library.Organize;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Library.Migration;

/// <summary>
/// 0.12: the migration wizard and the storage locations. A path mapping to a folder outside them is refused with the
/// code "outside-storage" and every such folder, so the wizard can offer the owner to allow them in one click; the old
/// app's own folders outside them are reported (and not looked at) instead of failing later during the take-over.
/// </summary>
public sealed class MigrationStorageApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LibraryFakeTmdb _tmdb = new();
    private readonly StreamZhFactory _factory;
    private readonly FakeArr _radarr = new("radarr");
    private readonly string _media = Directory.CreateTempSubdirectory("streamzh-migstorage-").FullName;

    public MigrationStorageApiTests() => _factory = new StreamZhFactory(_tmdb) { TempFolderIsStorage = false };

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_media, recursive: true);
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddHttpClient<ArrApiClient>().ConfigurePrimaryHttpMessageHandler(() => _radarr);
            services.Replace(ServiceDescriptor.Singleton<ITmdbFindClient>(_tmdb));
        }));
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = StreamZhFactory.OwnerName, password = StreamZhFactory.OwnerPassword })).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task A_mapping_outside_the_storage_locations_names_the_folders_and_works_once_the_owner_allows_them()
    {
        var client = await OwnerAsync();
        var second = Path.Combine(_media, "second");
        var request = new
        {
            url = "http://radarr:7878",
            apiKey = "secret",
            pathMappings = new[] { new { remote = "/movies", local = _media }, new { remote = "/other", local = second }, new { remote = "/again", local = _media } },
        };

        var refused = await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", request);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AppPaths.OutsideStorageCode, problem.GetProperty("code").GetString());
        Assert.Equal([_media, second], problem.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("outside the storage locations", problem.GetProperty("errors").GetProperty("pathMappings")[0].GetString(), StringComparison.Ordinal);

        // Starting a run is refused the same way, before anything is stored.
        var run = await client.PostAsJsonAsync("/api/v1/migration/radarr/run", new { request.url, request.apiKey, request.pathMappings, sections = new[] { "movies" } });
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Equal(AppPaths.OutsideStorageCode, (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // What the wizard's "Allow as storage locations" does.
        (await client.PostAsJsonAsync("/api/v1/settings/storage", new { path = _media })).EnsureSuccessStatusCode();

        var preview = await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", request);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var paths = (await preview.Content.ReadFromJsonAsync<MigrationPreview>(Json))!.Paths;
        var movies = Assert.Single(paths, p => p.Remote == "/movies");
        Assert.True(movies.Exists);
        Assert.False(movies.OutsideStorage);
    }

    [Fact]
    public async Task The_old_apps_own_folders_outside_the_storage_locations_are_reported_and_not_looked_at()
    {
        var client = await OwnerAsync();
        var looked = new List<string>();
        var mapper = new PathMapper([], exists: folder => { looked.Add(folder); return true; }, storageProblem: folder => folder == "/movies" ? "outside" : null);
        mapper.Use("/movies");
        mapper.Use("/inside");

        var checks = mapper.Checks();

        Assert.Equal(["/inside"], looked);
        Assert.True(Assert.Single(checks, c => c.Remote == "/movies").OutsideStorage);
        Assert.False(Assert.Single(checks, c => c.Remote == "/movies").Exists);

        // Through the API: Radarr's root folder /movies without a mapping.
        var preview = await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", new { url = "http://radarr:7878", apiKey = "secret" });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var result = (await preview.Content.ReadFromJsonAsync<MigrationPreview>(Json))!;
        Assert.True(Assert.Single(result.Paths, p => p.Remote == "/movies").OutsideStorage);
        Assert.Contains(result.Warnings, w => w.Contains("outside the storage locations", StringComparison.Ordinal));
    }

    /// <summary>
    /// SV3 W-01: the old app reported a movie folder outside every storage location (an Admin controls what "Radarr"
    /// says); it was stored unchecked and "delete with files" then removed that folder tree. Now the movie is not taken
    /// over, and the folder stays.
    /// </summary>
    [Fact]
    public async Task A_movie_folder_outside_the_storage_locations_is_not_taken_over()
    {
        var client = await OwnerAsync();
        var victim = Directory.CreateTempSubdirectory("streamzh-victim-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(victim, "unrelated.txt"), "keep me");
            var movies = JsonNode.Parse(FakeArr.Fixture("radarr-movie.json"))!.AsArray();
            var dune = movies.Single(m => m!["tmdbId"]!.GetValue<int>() == 693134)!;
            dune["path"] = victim;
            dune["folderName"] = victim;
            dune["hasFile"] = false;
            dune.AsObject().Remove("movieFile");
            _radarr.Overrides["/movie"] = movies.ToJsonString();
            (await client.PostAsJsonAsync("/api/v1/settings/storage", new { path = _media })).EnsureSuccessStatusCode();
            var request = new { url = "http://radarr:7878", apiKey = "secret", pathMappings = new[] { new { remote = "/movies", local = _media } } };

            var started = await client.PostAsJsonAsync("/api/v1/migration/radarr/run", new
            {
                request.url,
                request.apiKey,
                request.pathMappings,
                sections = new[] { "movies" },
                options = new Dictionary<string, string> { ["movies.monitor"] = "asIs" },
            });
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var run = (await started.Content.ReadFromJsonAsync<MigrationRun>(Json))!;
            for (var i = 0; i < 400 && run.Finished is null; i++)
            {
                await Task.Delay(25);
                run = (await client.GetFromJsonAsync<MigrationRun>($"/api/v1/migration/runs/{run.Id}", Json))!;
            }

            var section = run.Report.Sections.Single(s => s.Key == "movies");
            Assert.True(section.Failed >= 1);
            Assert.Contains(section.Messages, m => m.Text.Contains("outside the storage locations", StringComparison.Ordinal));
            var library = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies", Json);
            Assert.DoesNotContain(library.EnumerateArray(), m => m.GetProperty("tmdbId").GetInt32() == 693134);
            Assert.True(File.Exists(Path.Combine(victim, "unrelated.txt")));
        }
        finally
        {
            Directory.Delete(victim, recursive: true);
        }
    }

    /// <summary>SV3 W-01, defence in depth: a stored folder outside the storage locations is never deleted or moved away.</summary>
    [Fact]
    public void A_stored_folder_outside_the_storage_locations_is_never_deleted_or_moved()
    {
        var data = Path.Combine(_media, "data");
        var inside = Path.Combine(_media, "media", "Movie (2009)");
        var outside = Directory.CreateTempSubdirectory("streamzh-outside-").FullName;
        try
        {
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(inside);
            File.WriteAllText(Path.Combine(outside, "unrelated.txt"), "keep me");
            var paths = new AppPaths(data);
            paths.SetStorageLocations([Path.Combine(_media, "media")]);
            var mover = new FolderMover(new ImportFileSystem(NullLogger<ImportFileSystem>.Instance, paths), NullLogger<FolderMover>.Instance);
            var settings = new MediaManagementValues();

            var refused = Assert.Throws<LibraryException>(() => mover.RecycleFolder(outside, settings));
            Assert.Contains("does not move or delete", refused.Message, StringComparison.Ordinal);
            Assert.Throws<LibraryException>(() => mover.MoveFolder(outside, Path.Combine(_media, "media", "Moved"), settings));
            Assert.True(File.Exists(Path.Combine(outside, "unrelated.txt")));

            Assert.Null(mover.RecycleFolder(inside, settings));
            Assert.False(Directory.Exists(inside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>
    /// SV3 W-03: the preview no longer looks at the old app's folders outside the storage locations (on Windows, looking
    /// at \\host\share sends the account's NTLM credentials to that host); the root folder is skipped with the reason.
    /// </summary>
    [Fact]
    public async Task The_preview_does_not_look_at_old_app_folders_outside_the_storage_locations()
    {
        var outside = Directory.CreateTempSubdirectory("streamzh-oldroot-").FullName;
        try
        {
            var looked = new List<string>();
            var mapper = new PathMapper([], storageProblem: f => f.StartsWith(outside, StringComparison.Ordinal) ? "outside" : null);
            Assert.False(mapper.FolderExists(outside));
            Assert.False(mapper.FileExists(Path.Combine(outside, "x.mkv")));
            Assert.True(mapper.FolderExists(_media));

            var client = await OwnerAsync();
            _radarr.Overrides["/rootfolder"] = $$"""[{"id":1,"path":{{JsonSerializer.Serialize(outside)}},"accessible":true,"freeSpace":1,"unmappedFolders":[]}]""";
            var response = await client.PostAsJsonAsync("/api/v1/migration/radarr/preview", new { url = "http://radarr:7878", apiKey = "secret" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = (await response.Content.ReadFromJsonAsync<MigrationPreview>(Json))!;
            var root = Assert.Single(preview.Sections.Single(s => s.Key == "rootFolders").Items);
            Assert.Equal(MigrationAction.Skip, root.Action);
            Assert.Contains("outside the storage locations", root.Reason, StringComparison.Ordinal);
            Assert.True(Assert.Single(preview.Paths, p => p.Remote == outside).OutsideStorage);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>SV3 W-04: a storage location that holds a movie folder outside its root folder cannot be removed.</summary>
    [Fact]
    public async Task A_storage_location_holding_a_taken_over_movie_folder_stays()
    {
        var client = await OwnerAsync();
        var library = Path.Combine(_media, "library");
        var elsewhere = Path.Combine(_media, "elsewhere");
        Directory.CreateDirectory(Path.Combine(library, "movies"));
        Directory.CreateDirectory(Path.Combine(elsewhere, "Dune Part Two (2024)"));
        (await client.PostAsJsonAsync("/api/v1/settings/storage", new { path = library })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/settings/storage", new { path = elsewhere })).EnsureSuccessStatusCode();
        var movies = JsonNode.Parse(FakeArr.Fixture("radarr-movie.json"))!.AsArray();
        var dune = movies.Single(m => m!["tmdbId"]!.GetValue<int>() == 693134)!;
        dune["path"] = "/elsewhere/Dune Part Two (2024)";
        dune["hasFile"] = false;
        dune.AsObject().Remove("movieFile");
        _radarr.Overrides["/movie"] = new JsonArray(dune.DeepClone()).ToJsonString();
        var mappings = new[] { new { remote = "/movies", local = Path.Combine(library, "movies") }, new { remote = "/elsewhere", local = elsewhere } };

        var started = await client.PostAsJsonAsync("/api/v1/migration/radarr/run", new
        {
            url = "http://radarr:7878",
            apiKey = "secret",
            pathMappings = mappings,
            sections = new[] { "rootFolders", "movies" },
            options = new Dictionary<string, string> { ["movies.monitor"] = "asIs" },
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<MigrationRun>(Json))!;
        for (var i = 0; i < 400 && run.Finished is null; i++)
        {
            await Task.Delay(25);
            run = (await client.GetFromJsonAsync<MigrationRun>($"/api/v1/migration/runs/{run.Id}", Json))!;
        }

        Assert.Equal(1, run.Report.Sections.Single(s => s.Key == "movies").Created);
        var removed = await client.DeleteAsync($"/api/v1/settings/storage?path={Uri.EscapeDataString(elsewhere)}");
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.Contains("Dune", await removed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
