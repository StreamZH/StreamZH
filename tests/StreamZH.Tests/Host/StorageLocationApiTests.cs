// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core;
using StreamZH.Core.Users;

namespace StreamZH.Tests.Host;

/// <summary>
/// 0.12, second verification (SV2 N-03, N-06, N-01): an Admin pointed a download category at the service account's
/// home, uploaded an NZB titled ".ssh" holding "authorized_keys" and got an SSH login; or at the program folder for a
/// "tools" job; or made the host contact \\attacker\share. Every folder an Admin sets must now be inside one of the
/// owner's storage locations, judged before the folder is opened. This instance is a new installation: its only storage
/// location is the default download folder.
/// </summary>
public sealed class StorageLocationApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new() { TempFolderIsStorage = false };
    private readonly string _outside = Directory.CreateTempSubdirectory("streamzh-outside-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_outside, recursive: true);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty(field)[0].GetString()!;
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_new_installation_has_the_download_folder_as_its_storage_location()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var storage = await owner.GetFromJsonAsync<JsonObject>("/api/v1/settings/storage");
        var paths = _factory.Services.GetRequiredService<AppPaths>();

        Assert.Equal([paths.DownloadsDirectory], storage!["locations"]!.AsArray().Select(l => l!["path"]!.GetValue<string>()));
        Assert.True(storage["canChange"]!.GetValue<bool>());
        Assert.Equal(paths.DownloadsDirectory, storage["defaultDownloadFolder"]!.GetValue<string>());
    }

    /// <summary>The exploit: an Admin's category, download, root, renamer and mapping folders outside the storage locations.</summary>
    [Fact]
    public async Task Admin_folders_outside_the_storage_locations_are_refused()
    {
        await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin", UserRole.Admin);
        var home = Path.Combine(_outside, "home");
        Directory.CreateDirectory(home);

        Assert.Contains("outside the storage locations", await ErrorAsync(await admin.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "home", folder = home }), "folder"), StringComparison.Ordinal);
        Assert.Contains("outside the storage locations", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = home }), "completeFolder"), StringComparison.Ordinal);
        Assert.Contains("outside the storage locations", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { watchFolder = home }), "watchFolder"), StringComparison.Ordinal);

        var root = await admin.PostAsJsonAsync("/api/v1/rootfolders", new { path = home, mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.BadRequest, root.StatusCode);
        Assert.Equal(AppPaths.OutsideStorageCode, (await root.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var media = await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management");
        media!["recycleBin"] = home;
        Assert.Contains("outside the storage locations", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/media-management", media), "recycleBin"), StringComparison.Ordinal);

        Assert.Contains("outside the storage locations", await ErrorAsync(
            await admin.PostAsync("/api/v1/renamer/scan", Json(new { source = new { path = home, subfolders = true }, filter = new { } })), "source.path"), StringComparison.Ordinal);
        Assert.Contains("outside the storage locations", await ErrorAsync(
            await admin.PostAsJsonAsync("/api/v1/remotepathmapping", new { host = "box", remotePath = "/downloads", localPath = home }), "localPath"), StringComparison.Ordinal);

        // Inside the default download folder it works as before.
        var inside = Path.Combine(_factory.Services.GetRequiredService<AppPaths>().DownloadsDirectory, "movies-done");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = inside })).StatusCode);
    }

    /// <summary>Only the owner changes the storage locations; then the folder can be used (the one-click path of the web app).</summary>
    [Fact]
    public async Task Only_the_owner_adds_and_removes_storage_locations()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin", UserRole.Admin);
        var media = Directory.CreateDirectory(Path.Combine(_outside, "media")).FullName;
        var movies = Directory.CreateDirectory(Path.Combine(media, "Movies")).FullName;

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/settings/storage", new { path = media })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateApiKeyClient().PostAsJsonAsync("/api/v1/settings/storage", new { path = media })).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings/storage"))!["canChange"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/settings/storage", new { path = media })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/rootfolders", new { path = movies, mediaType = "Movie" })).StatusCode);

        // Still used by the root folder: not removed; an admin may not remove it at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/v1/settings/storage?path={Uri.EscapeDataString(media)}")).StatusCode);
        var used = await owner.DeleteAsync($"/api/v1/settings/storage?path={Uri.EscapeDataString(media)}");
        Assert.Equal(HttpStatusCode.Conflict, used.StatusCode);
        Assert.Contains(movies, await used.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>N-06: the folder browser lists only the storage locations for admins and never opens a path outside them (\\attacker\share).</summary>
    [Fact]
    public async Task Admins_browse_only_inside_the_storage_locations()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin", UserRole.Admin);
        var downloads = _factory.Services.GetRequiredService<AppPaths>().DownloadsDirectory;

        var roots = await admin.GetFromJsonAsync<JsonObject>("/api/v1/filesystem");
        Assert.Equal([downloads], roots!["folders"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));

        foreach (var path in new[] { _outside, "/", "//attacker.example/share", @"\\attacker.example\share", Path.Combine(downloads, "..", "keys") })
        {
            // Refused before it is opened (403), or not a full path on this system (400); never "does not exist" (404).
            var response = await admin.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(path)}");
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{path}: {response.StatusCode}");
        }

        var inside = await admin.GetFromJsonAsync<JsonObject>($"/api/v1/filesystem?path={Uri.EscapeDataString(downloads)}");
        Assert.Null(inside!["parent"]);

        // The owner browses everything (to choose storage locations).
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/filesystem?path={Uri.EscapeDataString(_outside)}")).StatusCode);
    }

    /// <summary>N-01: the folder StreamZH runs from is never a download or media folder, even inside a storage location.</summary>
    [Fact]
    public async Task The_program_folder_is_refused()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var app = _factory.Services.GetRequiredService<AppPaths>().AppDirectory;

        // The owner may allow anything, the program folder included, but no folder in it can be used.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/settings/storage", new { path = app })).StatusCode);
        Assert.Contains("folder StreamZH runs from", await ErrorAsync(await owner.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "app", folder = app }), "folder"), StringComparison.Ordinal);
        Assert.Contains("folder StreamZH runs from", await ErrorAsync(await owner.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = Path.Combine(app, "tools") }), "completeFolder"), StringComparison.Ordinal);
    }
}
