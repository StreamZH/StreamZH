// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamZH.Data;

/// <summary>Encrypts and decrypts one secret (the app uses ASP.NET Data Protection; see the host).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <exception cref="CryptographicException">The key is missing (other installation, lost key folder) or the value was changed.</exception>
    string Unprotect(string protectedValue);
}

/// <summary>Where an unreadable secret is stored, for the status notice: "Indexer", "NZBgeek", "API key".</summary>
public sealed record SecretLocation(string Kind, string Item, string Field);

/// <summary>
/// 0.12: secrets at rest. Repositories store passwords, API keys and tokens as <c>enc:v1:&lt;payload&gt;</c> and read
/// them back transparently. Without a protector (tests that create a <see cref="Database"/> on their own) values are
/// written as they are; the app always sets one at start (<see cref="Database.UseSecretProtector"/>) and then encrypts
/// what is still stored in plain text (<see cref="SecretMigration"/>). A value that cannot be decrypted (key folder
/// missing) reads as "not set" and is listed in <see cref="Unreadable"/>, so the item asks for it again instead of failing.
/// </summary>
public sealed class SecretCodec
{
    public const string Prefix = "enc:v1:";

    private readonly ConcurrentDictionary<SecretLocation, byte> _unreadable = new();
    private ISecretProtector? _protector;

    /// <summary>True when values are encrypted on write.</summary>
    public bool Encrypts => _protector is not null;

    /// <summary>Stored secrets that could not be decrypted since the start (cleared when the item is saved again).</summary>
    public IReadOnlyCollection<SecretLocation> Unreadable => [.. _unreadable.Keys.OrderBy(l => l.Kind).ThenBy(l => l.Item).ThenBy(l => l.Field)];

    public static bool IsEncrypted(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    internal void Use(ISecretProtector protector) => _protector = protector;

    /// <summary>The value as it is stored: encrypted when a protector is set; null and "" stay as they are.</summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsEncrypted(plaintext) || _protector is null)
        {
            return plaintext;
        }

        return Prefix + _protector.Protect(plaintext);
    }

    /// <summary>
    /// The plain value of a stored one. Plain text (from before 0.12) passes through. Null when it cannot be decrypted;
    /// then <paramref name="where"/> is remembered for the status notice.
    /// </summary>
    public string? Decrypt(string? stored, SecretLocation where)
    {
        ArgumentNullException.ThrowIfNull(where);
        if (!IsEncrypted(stored))
        {
            return stored;
        }

        if (_protector is not null)
        {
            try
            {
                var value = _protector.Unprotect(stored![Prefix.Length..]);
                _unreadable.TryRemove(where, out _);
                return value;
            }
            catch (CryptographicException)
            {
            }
            catch (FormatException)
            {
            }
        }

        _unreadable[where] = 0;
        return null;
    }

    /// <summary>The item was saved again: whatever was unreadable there is replaced.</summary>
    public void Forget(string kind, string item)
    {
        foreach (var location in _unreadable.Keys.Where(l => l.Kind == kind && l.Item == item))
        {
            _unreadable.TryRemove(location, out _);
        }
    }

    /// <summary>JSON settings: the string values of <see cref="SecretFields.Names"/> encrypted.</summary>
    public Dictionary<string, JsonElement> EncryptSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new Dictionary<string, JsonElement>(settings.Count, StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            result[key] = SecretFields.Names.Contains(key) && value.ValueKind == JsonValueKind.String && Encrypt(value.GetString()) is { } encrypted && encrypted != value.GetString()
                ? JsonSerializer.SerializeToElement(encrypted)
                : value;
        }

        return result;
    }

    /// <summary>JSON settings: every encrypted string value decrypted; an unreadable one is left out (= not set).</summary>
    public Dictionary<string, JsonElement> DecryptSettings(IReadOnlyDictionary<string, JsonElement> settings, string kind, string item)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new Dictionary<string, JsonElement>(settings.Count, StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            if (value.ValueKind == JsonValueKind.String && IsEncrypted(value.GetString()))
            {
                if (Decrypt(value.GetString(), new SecretLocation(kind, item, key)) is { } plain)
                {
                    result[key] = JsonSerializer.SerializeToElement(plain);
                }

                continue;
            }

            result[key] = value;
        }

        return result;
    }
}

/// <summary>Which values are secrets.</summary>
public static class SecretFields
{
    /// <summary>
    /// Keys of secret values in the JSON settings of connections, import lists and download clients (every field of type
    /// password or secret in their catalogs; a test checks that none is missing).
    /// </summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(
        ["password", "apiKey", "authToken", "accessToken", "token", "botToken", "appToken", "userKey", "webHookUrl", "statelessUrls", "clientId"],
        StringComparer.Ordinal);

    /// <summary>
    /// Config keys whose values are secrets. The waiting releases hold download links with indexer keys. The SSL
    /// certificate password is not among them: it is read before the keys are loaded.
    /// </summary>
    public static IReadOnlySet<string> ConfigKeys { get; } = new HashSet<string>(
        [Data.ConfigKeys.ApiKey, Data.ConfigKeys.TmdbToken, Data.ConfigKeys.ProxyPassword, Data.ConfigKeys.DiscordBotToken, Data.ConfigKeys.PendingReleases],
        StringComparer.Ordinal);
}
