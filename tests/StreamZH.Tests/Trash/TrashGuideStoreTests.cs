// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Data;
using StreamZH.Library.Trash;

namespace StreamZH.Tests.Trash;

public sealed class TrashGuideStoreTests : IDisposable
{
    private const string Commit1 = "529bd9c29eb41f38e36c14c41a137f4d5d824c99";
    private const string Commit2 = "1111111111111111111111111111111111111111";

    private readonly TrashTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task Downloads_the_branch_archive_and_keeps_only_the_guide_data()
    {
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Null(result.Error);
        Assert.Equal(Commit1, result.Commit);
        Assert.Equal("https://github.com/TRaSH-Guides/Guides/archive/refs/heads/master.zip", _bed.Http.Requests.Single().RequestUri!.ToString());
        var folder = _bed.Store.CurrentFolder!;
        Assert.Equal(Path.Combine(_bed.Store.Root, Commit1), folder);
        Assert.True(File.Exists(Path.Combine(folder, "metadata.json")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "json", "radarr", "cf", "german-dl.json")));
        Assert.True(File.Exists(Path.Combine(folder, "docs", "Radarr", "Radarr-collection-of-custom-formats.md")));
        Assert.False(File.Exists(Path.Combine(folder, "LICENSE")));
        Assert.False(Directory.Exists(Path.Combine(folder, "docs", "images")));
        Assert.False(File.Exists(Path.Combine(_bed.Store.Root, "escape.json")));
        Assert.Empty(Directory.EnumerateFiles(_bed.Store.Root, "*.zip"));
        Assert.Equal(2, _bed.Store.Current.Movie.Profiles.Count);
        Assert.Equal(TrashTestBed.Now, _bed.Store.LastSync);
    }

    [Fact]
    public async Task Unchanged_commit_skips_the_download()
    {
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);
        await _bed.Store.DownloadAsync(CancellationToken.None);
        _bed.Http.Requests.Clear();
        _bed.Http.ApiCommit = Commit1;

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.False(result.Changed);
        var request = Assert.Single(_bed.Http.Requests);
        Assert.Equal("https://api.github.com/repos/TRaSH-Guides/Guides/commits/master", request.RequestUri!.ToString());
        Assert.Contains("application/vnd.github.sha", request.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unchanged_etag_skips_the_download_when_the_api_is_not_available()
    {
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);
        await _bed.Store.DownloadAsync(CancellationToken.None);
        _bed.Http.Requests.Clear();
        _bed.Http.ApiCommit = null; // rate limited

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Null(result.Error);
        var archive = _bed.Http.Requests.Last();
        Assert.Equal("\"etag-1\"", archive.Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(Commit1, _bed.Store.Commit);
    }

    [Fact]
    public async Task New_commit_replaces_the_copy_and_removes_the_old_one()
    {
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);
        await _bed.Store.DownloadAsync(CancellationToken.None);
        _bed.Http.ApiCommit = Commit2;
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit2);
        _bed.Http.ETag = "\"etag-2\"";

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(Commit2, _bed.Store.Commit);
        Assert.Equal([Commit2], Directory.EnumerateDirectories(_bed.Store.Root).Select(Path.GetFileName));
        Assert.Equal("\"etag-2\"", _bed.Config.Get(ConfigKeys.TrashETag));
    }

    [Fact]
    public async Task Failed_download_keeps_the_last_good_copy_and_reports_the_error()
    {
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);
        await _bed.Store.DownloadAsync(CancellationToken.None);
        _bed.Http.ApiCommit = Commit2;
        _bed.Http.ArchiveStatus = HttpStatusCode.InternalServerError;

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("HTTP 500", result.Error, StringComparison.Ordinal);
        Assert.Equal(result.Error, _bed.Store.LastError);
        Assert.Equal(Commit1, _bed.Store.Commit);
        Assert.Equal(2, _bed.Store.Current.Series.Profiles.Count);

        // The next good sync clears the error.
        _bed.Http.ArchiveStatus = null;
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit2);
        Assert.Null((await _bed.Store.DownloadAsync(CancellationToken.None)).Error);
        Assert.Null(_bed.Store.LastError);
    }

    [Fact]
    public async Task Archive_without_guide_data_is_rejected()
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("Other-main/README.md");
        }

        _bed.Http.Archive = memory.ToArray();

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.Contains("metadata.json", result.Error, StringComparison.Ordinal);
        Assert.False(_bed.Store.HasCopy);
        Assert.Empty(Directory.EnumerateDirectories(_bed.Store.Root));
        Assert.Same(TrashGuides.Empty, _bed.Store.Current);
    }

    [Fact]
    public async Task Unknown_branch_is_a_clear_error()
    {
        _bed.Settings.Save(TrashSettings.Defaults with { Branch = "nope" });

        var result = await _bed.Store.DownloadAsync(CancellationToken.None);

        Assert.Contains("was not found", result.Error, StringComparison.Ordinal);
        Assert.Contains("/archive/refs/heads/nope.zip", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_folder_as_repository()
    {
        await _bed.UseLocalCopyAsync();

        Assert.True(_bed.Store.HasCopy);
        Assert.Empty(_bed.Http.Requests);
        Assert.Equal(2, _bed.Store.Current.Movie.Profiles.Count);
        Assert.False(File.Exists(Path.Combine(_bed.Store.CurrentFolder!, "LICENSE")));
    }

    [Fact]
    public async Task Scheduled_sync_downloads_only_once_the_guides_are_used()
    {
        var services = new ServiceCollection()
            .AddSingleton(_bed.Store).AddSingleton(_bed.Profiles).AddSingleton(_bed.Sync)
            .BuildServiceProvider();
        var task = new TrashSyncTask(_bed.Settings);
        _bed.Http.Archive = TrashTestBed.ArchiveOfFixture(Commit1);

        await task.RunAsync(services, CancellationToken.None);
        Assert.Empty(_bed.Http.Requests);
        Assert.Equal(TimeSpan.FromHours(24), task.Interval);

        // A profile that follows a template (e.g. from a restored backup without the copy) makes the sync download.
        _bed.ApplyGerman();
        await task.RunAsync(services, CancellationToken.None);
        Assert.True(_bed.Store.HasCopy);

        _bed.Settings.Save(TrashSettings.Defaults with { Enabled = false });
        Assert.Null(task.Interval);
    }

    [Theory]
    [InlineData("https://github.com/TRaSH-Guides/Guides", "master", "https://github.com/TRaSH-Guides/Guides/archive/refs/heads/master.zip")]
    [InlineData("https://github.com/me/Guides.git/", "feature/x", "https://github.com/me/Guides/archive/refs/heads/feature/x.zip")]
    public void Archive_url(string repository, string branch, string expected) =>
        Assert.Equal(expected, TrashGuideStore.ArchiveUrl(repository, branch));

    [Fact]
    public void Commit_check_only_for_github()
    {
        Assert.Equal("https://api.github.com/repos/a/b/commits/main", TrashGuideStore.GitHubApiCommitUrl("https://github.com/a/b", "main"));
        Assert.Null(TrashGuideStore.GitHubApiCommitUrl("https://gitea.example.com/a/b", "main"));
    }

    [Fact]
    public void Settings_validation()
    {
        Assert.Empty(TrashSettings.Validate(TrashSettings.Defaults));
        var errors = TrashSettings.Validate(new TrashSettingsValues(true, "ftp://x", "has space", 0, true, true));
        Assert.Equal(["branch", "repository", "syncIntervalHours"], errors.Keys.Order());
        Assert.Contains("repository", TrashSettings.Validate(TrashSettings.Defaults with { Repository = _bed.Root }).Keys);
        Assert.Empty(TrashSettings.Validate(TrashSettings.Defaults with { Repository = TrashTestBed.GuidesFolder }));
    }
}
