// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>Simple key/value settings store.</summary>
public sealed class ConfigRepository(Database database)
{
    public string? Get(string key)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<string>("SELECT Value FROM Config WHERE Key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO Config (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            new { key, value });
    }

    /// <summary>Returns the stored value or creates and stores a new one.</summary>
    public string GetOrCreate(string key, Func<string> factory)
    {
        var existing = Get(key);
        if (existing is not null)
        {
            return existing;
        }

        var created = factory();
        Set(key, created);
        return created;
    }
}