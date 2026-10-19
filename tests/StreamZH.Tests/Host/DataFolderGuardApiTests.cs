// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Downloads;

namespace StreamZH.Tests.Host;

/// <summary>
/// V-02: an Admin pointed a download category at &lt;data&gt;/tools, uploaded an NZB holding "par2" and got it run as the
/// service account. No folder StreamZH writes to may be inside the data folder (except downloads/ and Backups/) or contain
/// it: refused on save, and ignored when an older version or a restored database still has such a path.
/// </summary>
public sealed class DataFolderGuardApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string Data => Path.GetFullPath(_factory.DataDirectory);

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty(field)[0].GetString()!;
    }

    [Fact]
    public async Task Folders_inside_the_data_folder_are_refused_for_downloads_root_folders_recycle_bin_and_backups()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var admin = await _factory.CreateUserClientAsync("admin", UserRole.Admin);
        var tools = Path.Combine(Data, "tools");
        var keys = Path.Combine(Data, "keys");

        // The exploit: a category in <data>/tools.
        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await admin.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "evil", folder = tools }), "folder"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/settings/downloads/categories", new { name = "evil", folder = "../../tools" })).StatusCode);
        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = keys }), "completeFolder"), StringComparison.Ordinal);
        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { incompleteFolder = Data }), "incompleteFolder"), StringComparison.Ordinal);
        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { watchFolder = tools }), "watchFolder"), StringComparison.Ordinal);
        Assert.Contains("contains the StreamZH data folder", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = Path.GetDirectoryName(Data) }), "completeFolder"), StringComparison.Ordinal);

        Directory.CreateDirectory(tools);
        var root = await admin.PostAsJsonAsync("/api/v1/rootfolders", new { path = tools, mediaType = "Movie" });
        Assert.Equal(HttpStatusCode.BadRequest, root.StatusCode);
        Assert.Contains("inside the StreamZH data folder", await root.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var media = await admin.GetFromJsonAsync<JsonObject>("/api/v1/settings/media-management");
        media!["recycleBin"] = tools;
        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await admin.PutAsJsonAsync("/api/v1/settings/media-management", media), "recycleBin"), StringComparison.Ordinal);

        Assert.Contains("inside the StreamZH data folder", await ErrorAsync(await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = tools }), "backupFolder"), StringComparison.Ordinal);

        // Its own downloads folder stays fine.
        var own = Path.Combine(Data, "downloads", "movies-done");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/downloads", new { completeFolder = own })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = Path.Combine(Data, "Backups", "weekly") })).StatusCode);
    }

    [Fact]
    public async Task A_stored_folder_inside_the_data_folder_is_not_used()
    {
        await _factory.CreateOwnerClientAsync();
        var config = _factory.Services.GetRequiredService<ConfigRepository>();
        var categories = _factory.Services.GetRequiredService<DownloadCategoryService>();
        var paths = _factory.Services.GetRequiredService<AppPaths>();

        // As an older version or a restored database could have stored them.
        config.Set(ConfigKeys.CompleteFolder, paths.ToolsDirectory);
        config.Set(ConfigKeys.IncompleteFolder, paths.KeysDirectory);
        _factory.Services.GetRequiredService<DownloadCategoryRepository>().Add(new DownloadCategoryRecord(0, "evil", paths.ToolsDirectory, null, 3));
        categories.Invalidate();
        var settings = _factory.Services.GetRequiredService<StreamZH.Library.Search.DownloadSettings>();

        Assert.Equal(settings.DefaultCompleteFolder, settings.CompleteFolder);
        Assert.Equal(settings.DefaultIncompleteFolder, settings.IncompleteFolder);
        var folder = categories.FolderOf(categories.Find("evil")!);
        Assert.Equal(Path.Combine(settings.DefaultCompleteFolder, "evil"), folder);
        Assert.Null(paths.FolderProblem(folder));
    }
}
