// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;

namespace StreamZH.Data;

/// <summary>
/// Simple key/value settings store. Secret values (<see cref="SecretFields.ConfigKeys"/>: API key, TMDB token, proxy
/// password, Discord bot token) are stored encrypted (0.12) and read back in plain text; one that cannot be decrypted
/// reads as null.
/// </summary>
public sealed class ConfigRepository(Database database)
{
    public const string SettingKind = "Setting";

    public string? Get(string key)
    {
        var stored = GetStored(key);
        return SecretFields.ConfigKeys.Contains(key) ? database.Secrets.Decrypt(stored, new SecretLocation(SettingKind, key, key)) : stored;
    }

    public void Set(string key, string value)
    {
        var secret = SecretFields.ConfigKeys.Contains(key);
        var stored = secret ? database.Secrets.Encrypt(value) : value;
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO Config (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            new { key, value = stored });
        if (secret)
        {
            database.Secrets.Forget(SettingKind, key);
        }
    }

    /// <summary>Returns the stored value or creates and stores a new one. A stored secret that cannot be decrypted is kept (null is returned).</summary>
    public string? GetOrCreate(string key, Func<string> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (GetStored(key) is not null)
        {
            return Get(key);
        }

        var created = factory();
        Set(key, created);
        return created;
    }

    /// <summary>The value as it is in the database (secrets encrypted).</summary>
    public string? GetStored(string key)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<string>("SELECT Value FROM Config WHERE Key = @key", new { key });
    }
}