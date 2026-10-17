// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;

namespace StreamZH.Tests.Data;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void Migrates_and_stores_config()
    {
        var database = new Database(_file);
        database.Migrate();
        database.Migrate(); // idempotent

        var config = new ConfigRepository(database);
        config.Set("Theme", "dark");
        config.Set("Theme", "light");

        Assert.Equal(1, database.SchemaVersion());
        Assert.Equal("light", config.Get("Theme"));
        Assert.Equal("abc", config.GetOrCreate("ApiKey", () => "abc"));
        Assert.Equal("abc", config.GetOrCreate("ApiKey", () => "other"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}