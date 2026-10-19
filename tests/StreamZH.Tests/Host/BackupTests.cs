// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Backups;
using StreamZH.Host.General;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Host;

/// <summary>System → Backup: create, retention, validation, staged restore applied at the next start.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-backup-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly ConfigRepository _config;
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly BackupService _backups;
    private readonly BackupSettings _settings;

    public BackupTests()
    {
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _database = new Database(_paths.DatabaseFile);
        _database.Migrate();
        _config = new ConfigRepository(_database);
        _settings = new BackupSettings(new SettingsCache(_config), _paths);
        _backups = new BackupService(_settings, _paths, _database, _time, NullLogger<BackupService>.Instance);
    }

    [Fact]
    public void A_backup_contains_the_database_the_keys_and_a_manifest()
    {
        _config.Set(ConfigKeys.InstanceName, "Backup me");
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        File.WriteAllText(Path.Combine(_dir, "keys", "key-1.xml"), "<key/>");

        var backup = _backups.Create(BackupType.Manual);

        Assert.Equal("manual", backup.Type);
        Assert.StartsWith($"streamzh_backup_v{AppInfo.Version}_", backup.Name, StringComparison.Ordinal);
        var path = _backups.PathOf("manual", backup.Name)!;
        Assert.StartsWith(Path.Combine(_dir, "Backups", "manual"), path, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            // S3-07: a backup holds every secret; only StreamZH's account may read it.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("streamzh.db"));
            Assert.NotNull(zip.GetEntry("keys/key-1.xml"));
            Assert.NotNull(zip.GetEntry("backup.json"));
        }

        Assert.Null(_backups.Validate(path, out var manifest));
        Assert.Equal("manual", manifest!.Type);
        Assert.Equal(_database.SchemaVersion(), manifest.SchemaVersion);

        var second = _backups.Create(BackupType.Manual);
        Assert.NotEqual(backup.Name, second.Name);
        Assert.Equal(2, _backups.List().Count);
    }

    [Fact]
    public void Retention_removes_only_old_scheduled_backups()
    {
        _config.Set(ConfigKeys.BackupRetentionDays, "7");
        var old = _backups.Create(BackupType.Scheduled);
        _time.Now += TimeSpan.FromSeconds(1);
        var recent = _backups.Create(BackupType.Scheduled);
        var manual = _backups.Create(BackupType.Manual);
        File.SetLastWriteTimeUtc(_backups.PathOf("scheduled", old.Name)!, _time.Now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(_backups.PathOf("manual", manual.Name)!, _time.Now.UtcDateTime.AddDays(-30));

        Assert.Equal(1, _backups.Cleanup());

        Assert.Null(_backups.PathOf("scheduled", old.Name));
        Assert.NotNull(_backups.PathOf("scheduled", recent.Name));
        Assert.NotNull(_backups.PathOf("manual", manual.Name));
    }

    [Fact]
    public async Task The_backup_task_follows_the_interval_and_cleans_up()
    {
        var task = new BackupTask(_settings);
        Assert.Equal(TimeSpan.FromDays(7), task.Interval);
        _config.Set(ConfigKeys.BackupIntervalDays, "0");
        Assert.Equal(TimeSpan.FromDays(7), task.Interval);
        new SettingsCache(_config).Set(ConfigKeys.BackupIntervalDays, "0");
        var fresh = new BackupTask(new BackupSettings(new SettingsCache(_config), _paths));
        Assert.Null(fresh.Interval);

        var services = new ServiceCollection().AddSingleton(_backups).BuildServiceProvider();
        await task.RunAsync(services, CancellationToken.None);
        Assert.Single(_backups.List(), b => b.Type == "scheduled");
    }

    [Fact]
    public void Validation_rejects_other_zips_foreign_databases_and_newer_versions()
    {
        var notBackup = Path.Combine(_dir, "other.zip");
        using (var zip = ZipFile.Open(notBackup, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Contains("streamzh.db is missing", _backups.Validate(notBackup, out _), StringComparison.Ordinal);

        var foreignDb = Path.Combine(_dir, "foreign.db");
        using (var connection = new SqliteConnection($"Data Source={foreignDb};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Movies (Id INTEGER)";
            command.ExecuteNonQuery();
        }

        Assert.Contains("not a StreamZH database", _backups.Validate(Zip("foreign.zip", foreignDb), out _), StringComparison.Ordinal);

        var newer = Path.Combine(_dir, "newer.db");
        File.Copy(_paths.DatabaseFile, newer);
        using (var connection = new SqliteConnection($"Data Source={newer};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO VersionInfo (Version, AppliedOn, Description) VALUES (999999, '2030-01-01', 'Future')";
            command.ExecuteNonQuery();
        }

        Assert.Contains("newer StreamZH", _backups.Validate(Zip("newer.zip", newer), out _), StringComparison.Ordinal);

        var garbage = Path.Combine(_dir, "garbage.zip");
        File.WriteAllText(garbage, "not a zip");
        Assert.NotNull(_backups.Validate(garbage, out _));
    }

    [Fact]
    public void A_staged_restore_replaces_the_database_at_the_next_start_and_keeps_the_old_one()
    {
        _config.Set(ConfigKeys.InstanceName, "Before");
        var backup = _backups.Create(BackupType.Manual);
        _config.Set(ConfigKeys.InstanceName, "After");

        Assert.Null(_backups.StageRestore(_backups.PathOf("manual", backup.Name)!, backup.Name));
        Assert.Equal(backup.Name, _backups.Pending()!.Source);

        // Next start: connections closed, then the restore runs before the database is opened.
        SqliteConnection.ClearAllPools();
        var outcome = PendingRestore.Apply(_paths);

        Assert.True(outcome!.Success, outcome.Message);
        Assert.Null(_backups.Pending());
        var restored = new Database(_paths.DatabaseFile);
        restored.Migrate();
        Assert.Equal("Before", new ConfigRepository(restored).Get(ConfigKeys.InstanceName));
        Assert.True(File.Exists(Path.Combine(outcome.PreviousFolder!, "streamzh.db")));
        Assert.Null(PendingRestore.Apply(_paths));
    }

    [Fact]
    public void A_broken_staged_restore_keeps_the_current_database()
    {
        _config.Set(ConfigKeys.InstanceName, "Current");
        Directory.CreateDirectory(PendingRestore.Folder(_paths));
        File.WriteAllText(PendingRestore.ZipPath(_paths), "broken");

        SqliteConnection.ClearAllPools();
        var outcome = PendingRestore.Apply(_paths);

        Assert.False(outcome!.Success);
        Assert.Equal("Current", new ConfigRepository(new Database(_paths.DatabaseFile)).Get(ConfigKeys.InstanceName));
        Assert.False(File.Exists(PendingRestore.ZipPath(_paths)));
        Assert.Single(Directory.GetFiles(PendingRestore.Folder(_paths), "failed-*.zip"));
    }

    [Fact]
    public async Task Backup_api_creates_lists_downloads_restores_and_deletes()
    {
        using var factory = new StreamZhFactory();
        var owner = await factory.CreateOwnerClientAsync();

        var created = await owner.PostAsync("/api/v1/system/backup", null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var backup = (await created.Content.ReadFromJsonAsync<BackupInfo>())!;

        var list = await owner.GetFromJsonAsync<BackupList>("/api/v1/system/backup");
        Assert.Single(list!.Backups);
        Assert.Equal(7, list.IntervalDays);
        Assert.Equal(28, list.RetentionDays);

        var download = await owner.GetAsync($"/api/v1/system/backup/manual/{backup.Name}");
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/system/backup/manual/..%2Fstreamzh.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/system/backup/other/x.zip")).StatusCode);

        // Upload: garbage is refused, the real backup is staged until cancelled.
        var refused = await owner.PostAsync("/api/v1/system/backup/restore?name=x.zip", Content(System.Text.Encoding.UTF8.GetBytes("nope")));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var staged = await owner.PostAsync($"/api/v1/system/backup/restore?name={backup.Name}", Content(bytes));
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        Assert.Equal(backup.Name, (await owner.GetFromJsonAsync<BackupList>("/api/v1/system/backup"))!.PendingRestore!.Source);
        Assert.Contains("restore-pending", await owner.GetStringAsync("/api/v1/status/ongoing"), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/system/backup/restore")).StatusCode);
        Assert.Null((await owner.GetFromJsonAsync<BackupList>("/api/v1/system/backup"))!.PendingRestore);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/system/backup/manual/{backup.Name}/restore", null)).StatusCode);
        Assert.NotNull((await owner.GetFromJsonAsync<BackupList>("/api/v1/system/backup"))!.PendingRestore);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/system/backup/manual/{backup.Name}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<BackupList>("/api/v1/system/backup"))!.Backups);
    }

    [Fact]
    public async Task Backup_settings_are_validated()
    {
        using var factory = new StreamZhFactory();
        var owner = await factory.CreateOwnerClientAsync();

        var response = await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = "relative/folder", backupInterval = 31, backupRetention = 0 });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("backupFolder", text, StringComparison.Ordinal);
        Assert.Contains("backupInterval", text, StringComparison.Ordinal);
        Assert.Contains("backupRetention", text, StringComparison.Ordinal);

        var folder = Path.Combine(factory.DataDirectory, "Backups", "elsewhere");
        response = await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = folder, backupInterval = 1, backupRetention = 14 });
        var saved = await response.Content.ReadFromJsonAsync<GeneralSettingsResource>();
        Assert.Equal(folder, saved!.BackupFolder);
        Assert.Equal(1, saved.BackupInterval);

        var tasks = await owner.GetStringAsync("/api/v1/system/tasks");
        Assert.Contains("\"key\":\"backup\"", tasks, StringComparison.Ordinal);
        Assert.Contains("\"intervalMinutes\":1440", tasks, StringComparison.Ordinal);

        response = await owner.PutAsJsonAsync("/api/v1/settings/general", new { backupFolder = string.Empty });
        Assert.Equal(Path.Combine(factory.DataDirectory, "Backups"), (await response.Content.ReadFromJsonAsync<GeneralSettingsResource>())!.BackupFolder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ByteArrayContent Content(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return content;
    }

    private string Zip(string name, string databaseFile)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(databaseFile, "streamzh.db");
        return path;
    }
}
