// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Data.Migrations;
using StreamZH.Host.Auth;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Library.ImportLists;
using StreamZH.Library.Indexers;
using StreamZH.Library.Notifications;
using StreamZH.Library.Usenet;

namespace StreamZH.Tests.Data;

/// <summary>0.12: secrets at rest (encrypted with Data Protection), the migration of plain values, and lost keys.</summary>
public sealed class SecretStorageTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-secrets-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static ISecretProtector NewKeys() => new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());

    private Database Open(ISecretProtector? protector)
    {
        var database = new Database(_file);
        database.Migrate();
        if (protector is not null)
        {
            database.UseSecretProtector(protector);
        }

        return database;
    }

    private static Dictionary<string, JsonElement> Settings(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private static void AddEverything(Database database)
    {
        new ConfigRepository(database).Set(ConfigKeys.ApiKey, "api-key-value");
        new ConfigRepository(database).Set(ConfigKeys.ProxyPassword, "proxy-pass");
        new ConfigRepository(database).Set(ConfigKeys.InstanceName, "Media");
        new IndexerRepository(database).Add(IndexerService.Schema() with { Name = "Indexer", BaseUrl = "https://indexer.example", ApiKey = "indexer-key" }, DateTime.UtcNow);
        new UsenetServerRepository(database).Add(UsenetServerService.Schema() with { Name = "Provider", Host = "news.example.com", Username = "user", Password = "usenet-pass" }, DateTime.UtcNow);
        new DownloadClientRepository(database).Add(new DownloadClientDefinition(
            0, "qBittorrent", DownloadClientDefinition.QBittorrent, DownloadProtocol.Torrent, true, 1, Settings(new { host = "qbit.local", port = 8080, password = "qbit-pass" })));
        new ConnectionRepository(database).Add(
            new ConnectionDefinition(0, "Plex", "plex", true, new ConnectionTriggers(OnImport: true), Settings(new { host = "plex.local", port = 32400, authToken = "plex-token" })),
            DateTime.UtcNow);
    }

    private static string Raw(Database database, string sql)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<string>(sql) ?? string.Empty;
    }

    private static void AssertReadable(Database database)
    {
        Assert.Equal("api-key-value", new ConfigRepository(database).Get(ConfigKeys.ApiKey));
        Assert.Equal("proxy-pass", new ConfigRepository(database).Get(ConfigKeys.ProxyPassword));
        Assert.Equal("indexer-key", new IndexerRepository(database).All().Single().ApiKey);
        Assert.Equal("usenet-pass", new UsenetServerRepository(database).All().Single().Password);
        Assert.Equal("qbit-pass", new DownloadClientRepository(database).All().Single().Settings["password"].GetString());
        var plex = new ConnectionRepository(database).All().Single();
        Assert.Equal("plex-token", plex.Settings["authToken"].GetString());
        Assert.Equal("plex.local", plex.Settings["host"].GetString());
    }

    /// <summary>
    /// V-12: a ciphertext saved as a secret used to be stored unchanged and then decrypted on use, so whoever had a
    /// ciphertext could have it sent out in plain text. Now it is encrypted again and reads back as the text it was.
    /// </summary>
    [Fact]
    public void A_ciphertext_saved_as_a_secret_is_not_decrypted_on_use()
    {
        var database = Open(NewKeys());
        var config = new ConfigRepository(database);
        config.Set(ConfigKeys.ProxyPassword, "the-real-secret");
        var cipher = Raw(database, "SELECT Value FROM Config WHERE Key = 'ProxyPassword'");
        Assert.StartsWith(SecretCodec.Prefix, cipher, StringComparison.Ordinal);

        var hook = new ConnectionRepository(database);
        hook.Add(new ConnectionDefinition(0, "Hook", "webhook", true, new ConnectionTriggers(OnGrab: true), Settings(new { url = "https://attacker.example", password = cipher })), DateTime.UtcNow);
        Assert.Equal(cipher, hook.All().Single().Settings["password"].GetString());
        new IndexerRepository(database).Add(IndexerService.Schema() with { Name = "Indexer", BaseUrl = "https://attacker.example", ApiKey = cipher }, DateTime.UtcNow);
        Assert.Equal(cipher, new IndexerRepository(database).All().Single().ApiKey);
        Assert.NotEqual(cipher, Raw(database, "SELECT ApiKey FROM Indexers"));

        // The migration still leaves values that are already encrypted alone.
        Assert.Equal(0, SecretMigration.EncryptPlaintext(database));
        Assert.Equal("the-real-secret", config.Get(ConfigKeys.ProxyPassword));
    }

    [Fact]
    public void Secrets_are_stored_encrypted_and_read_back_in_plain_text()
    {
        var database = Open(NewKeys());
        AddEverything(database);

        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT Value FROM Config WHERE Key = 'ApiKey'"), StringComparison.Ordinal);
        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT Value FROM Config WHERE Key = 'ProxyPassword'"), StringComparison.Ordinal);
        Assert.Equal("Media", Raw(database, "SELECT Value FROM Config WHERE Key = 'InstanceName'"));
        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT ApiKey FROM Indexers"), StringComparison.Ordinal);
        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT Password FROM UsenetServers"), StringComparison.Ordinal);
        var client = Raw(database, "SELECT Settings FROM DownloadClients");
        Assert.DoesNotContain("qbit-pass", client, StringComparison.Ordinal);
        Assert.Contains("qbit.local", client, StringComparison.Ordinal);
        Assert.DoesNotContain("plex-token", Raw(database, "SELECT Settings FROM Connections"), StringComparison.Ordinal);
        AssertReadable(database);
        Assert.Empty(database.Secrets.Unreadable);

        // Saving again does not encrypt twice.
        var indexers = new IndexerRepository(database);
        indexers.Update(indexers.All().Single() with { Priority = 10 });
        Assert.Equal("indexer-key", indexers.All().Single().ApiKey);
    }

    [Fact]
    public void Plain_values_from_before_are_encrypted_once_at_start()
    {
        var old = Open(protector: null);
        AddEverything(old);
        Assert.Equal("indexer-key", Raw(old, "SELECT ApiKey FROM Indexers"));
        Assert.Equal("api-key-value", Raw(old, "SELECT Value FROM Config WHERE Key = 'ApiKey'"));

        var database = Open(NewKeys());
        Assert.Equal(6, SecretMigration.EncryptPlaintext(database));
        Assert.Equal(0, SecretMigration.EncryptPlaintext(database));
        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT ApiKey FROM Indexers"), StringComparison.Ordinal);
        Assert.StartsWith(SecretCodec.Prefix, Raw(database, "SELECT Value FROM Config WHERE Key = 'ApiKey'"), StringComparison.Ordinal);
        Assert.DoesNotContain("usenet-pass", Raw(database, "SELECT Password FROM UsenetServers"), StringComparison.Ordinal);
        AssertReadable(database);
        Assert.Empty(SecretMigration.FindUnreadable(database));
    }

    [Fact]
    public void Secrets_saved_with_other_keys_read_as_not_set_and_ask_to_be_entered_again()
    {
        AddEverything(Open(NewKeys()));
        var storedKey = Raw(Open(protector: null), "SELECT Value FROM Config WHERE Key = 'ApiKey'");

        // The key folder is gone (or the database was copied without it): nothing crashes.
        var database = Open(NewKeys());
        var config = new ConfigRepository(database);
        Assert.Null(config.Get(ConfigKeys.ApiKey));
        Assert.Equal(string.Empty, new IndexerRepository(database).All().Single().ApiKey);
        Assert.Equal(string.Empty, new UsenetServerRepository(database).All().Single().Password);
        Assert.False(new DownloadClientRepository(database).All().Single().Settings.ContainsKey("password"));
        Assert.Equal("plex.local", new ConnectionRepository(database).All().Single().Settings["host"].GetString());

        var unreadable = SecretMigration.FindUnreadable(database);
        Assert.Equal(
            ["Connection Plex authToken", "Download client qBittorrent password", "Indexer Indexer API key", "Setting ApiKey ApiKey", "Setting ProxyPassword ProxyPassword", "Usenet server Provider Password"],
            unreadable.Select(l => $"{l.Kind} {l.Item} {l.Field}").Order(StringComparer.Ordinal));

        // The API key is not replaced behind the owner's back; the encryption at start leaves the values alone.
        Assert.Null(config.GetOrCreate(ConfigKeys.ApiKey, () => "new"));
        Assert.Equal(storedKey, config.GetStored(ConfigKeys.ApiKey));
        Assert.Equal(0, SecretMigration.EncryptPlaintext(database));

        // Entered again: readable and no longer listed.
        var indexers = new IndexerRepository(database);
        indexers.Update(indexers.All().Single() with { ApiKey = "typed-again" });
        Assert.Equal("typed-again", indexers.All().Single().ApiKey);
        Assert.DoesNotContain(database.Secrets.Unreadable, l => l.Kind == IndexerRepository.SecretKind);
        config.Set(ConfigKeys.ApiKey, "new-key");
        Assert.Equal("new-key", config.Get(ConfigKeys.ApiKey));
    }

    [Fact]
    public void Every_secret_field_of_the_catalogs_is_encrypted()
    {
        var names = ConnectCatalog.All.SelectMany(t => t.Fields).Where(f => f.Type == ConnectFieldTypes.Password).Select(f => f.Name)
            .Concat(ImportListCatalog.All.SelectMany(t => t.AsConnectType().Fields).Where(f => f.Type == ConnectFieldTypes.Password).Select(f => f.Name))
            .Concat(DownloadClientCatalog.All.SelectMany(i => i.Fields).Where(f => f.Secret).Select(f => f.Name))
            .Distinct()
            .ToList();
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Contains(name, SecretFields.Names));
    }

    [Fact]
    public void Old_history_links_lose_their_keys()
    {
        var database = Open(protector: null);
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO History (Date, EventType, MediaType, Languages, SourceTitle, QualityId, Size, Data) VALUES (@date, 1, 1, '', 'Some.Movie', 0, 0, @data)",
            new
            {
                date = DateTime.UtcNow,
                data = JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["downloadUrl"] = "https://indexer.example/getnzb/a.nzb&i=1&r=0123456789abcdef",
                    ["guid"] = "https://indexer.example/api?t=get&id=a&apikey=0123456789abcdef",
                    ["nzbName"] = "Some.Movie",
                }),
            });
        connection.Execute("INSERT INTO History (Date, EventType, MediaType, Languages, SourceTitle, QualityId, Size, Data) VALUES (@date, 1, 1, '', 'Other', 0, 0, '{\"guid\":\"abc\"}')", new { date = DateTime.UtcNow });

        Assert.Equal(1, M028_RedactHistoryLinks.Redact(connection, null));
        Assert.Equal(0, M028_RedactHistoryLinks.Redact(connection, null));
        var data = connection.ExecuteScalar<string>("SELECT Data FROM History WHERE SourceTitle = 'Some.Movie'")!;
        Assert.DoesNotContain("0123456789abcdef", data, StringComparison.Ordinal);
        Assert.Equal("https://indexer.example/api?t=get&id=a&apikey=(removed)", JsonSerializer.Deserialize<Dictionary<string, string?>>(data)!["guid"]);
    }
}
