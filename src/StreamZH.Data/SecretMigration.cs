// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;

namespace StreamZH.Data;

/// <summary>
/// 0.12: encrypts secrets that are still stored in plain text: everything from before 0.12, a restored old backup, and
/// the API key created at the very first start. Runs at every start once the protector is set; values that are already
/// encrypted (<see cref="SecretCodec.Prefix"/>) are left alone, so it only ever changes plain values.
/// </summary>
public static class SecretMigration
{
    /// <summary>Encrypts what is still plain; returns how many values were encrypted. Does nothing without a protector.</summary>
    public static int EncryptPlaintext(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var codec = database.Secrets;
        if (!codec.Encrypts)
        {
            return 0;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var count = 0;

        foreach (var key in SecretFields.ConfigKeys)
        {
            var value = connection.QuerySingleOrDefault<string>("SELECT Value FROM Config WHERE Key = @key", new { key }, transaction);
            if (!string.IsNullOrEmpty(value) && !SecretCodec.IsEncrypted(value))
            {
                connection.Execute("UPDATE Config SET Value = @value WHERE Key = @key", new { key, value = codec.EncryptPlain(value) }, transaction);
                count++;
            }
        }

        count += EncryptColumn(connection, transaction, codec, "Indexers", "ApiKey");
        count += EncryptColumn(connection, transaction, codec, "UsenetServers", "Password");
        foreach (var table in new[] { "DownloadClients", "Connections", "ImportLists" })
        {
            count += EncryptSettings(connection, transaction, codec, table);
        }

        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Tries to decrypt every stored secret; the ones that fail end up in <see cref="SecretCodec.Unreadable"/> (status
    /// notice) and are returned. Nothing is changed in the database.
    /// </summary>
    public static IReadOnlyCollection<SecretLocation> FindUnreadable(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var codec = database.Secrets;
        using var connection = database.Open();
        foreach (var key in SecretFields.ConfigKeys)
        {
            codec.Decrypt(connection.QuerySingleOrDefault<string>("SELECT Value FROM Config WHERE Key = @key", new { key }), new SecretLocation(ConfigRepository.SettingKind, key, key));
        }

        foreach (var (name, value) in connection.Query<(string Name, string? Value)>("SELECT Name, ApiKey FROM Indexers"))
        {
            codec.Decrypt(value, new SecretLocation(IndexerRepository.SecretKind, name, "API key"));
        }

        foreach (var (name, value) in connection.Query<(string Name, string? Value)>("SELECT Name, Password FROM UsenetServers"))
        {
            codec.Decrypt(value, new SecretLocation(UsenetServerRepository.SecretKind, name, "Password"));
        }

        foreach (var (table, kind) in new[]
        {
            ("DownloadClients", DownloadClientRepository.SecretKind),
            ("Connections", ConnectionRepository.SecretKind),
            ("ImportLists", ImportListRepository.SecretKind),
        })
        {
            foreach (var (name, json) in connection.Query<(string Name, string? Settings)>($"SELECT Name, Settings FROM {table}"))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) is { } settings)
                    {
                        codec.DecryptSettings(settings, kind, name);
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        return codec.Unreadable;
    }

    // Table and column names are constants of this class, never input.
    private static int EncryptColumn(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, SecretCodec codec, string table, string column)
    {
        var rows = connection.Query<(long Id, string? Value)>($"SELECT Id, {column} FROM {table}", transaction: transaction);
        var count = 0;
        foreach (var (id, value) in rows.ToList())
        {
            if (!string.IsNullOrEmpty(value) && !SecretCodec.IsEncrypted(value))
            {
                connection.Execute($"UPDATE {table} SET {column} = @value WHERE Id = @id", new { id, value = codec.EncryptPlain(value) }, transaction);
                count++;
            }
        }

        return count;
    }

    private static int EncryptSettings(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, SecretCodec codec, string table)
    {
        var rows = connection.Query<(long Id, string? Settings)>($"SELECT Id, Settings FROM {table}", transaction: transaction);
        var count = 0;
        foreach (var (id, json) in rows.ToList())
        {
            Dictionary<string, JsonElement>? settings;
            try
            {
                settings = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (settings is null)
            {
                continue;
            }

            var plain = settings.Count(p => p.Value.ValueKind == JsonValueKind.String
                && p.Value.GetString() is { Length: > 0 } text && !SecretCodec.IsEncrypted(text) && SecretFields.IsSecret(p.Key, text));
            if (plain == 0)
            {
                continue;
            }

            connection.Execute(
                $"UPDATE {table} SET Settings = @settings WHERE Id = @id",
                new { id, settings = JsonSerializer.Serialize(codec.EncryptSettings(settings, migration: true)) },
                transaction);
            count += plain;
        }

        return count;
    }
}
