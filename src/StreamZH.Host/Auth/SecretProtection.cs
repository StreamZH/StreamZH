// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.DataProtection;
using StreamZH.Data;
using StreamZH.Host.Api;

namespace StreamZH.Host.Auth;

/// <summary>
/// Stored secrets encrypted with ASP.NET Data Protection, purpose <see cref="Purpose"/>: the same key ring as the
/// sign-in cookies (&lt;data&gt;/keys, see <see cref="AuthServiceExtensions"/>). Whoever has the database without the key
/// folder cannot read the secrets; a backup holds both and is therefore secret material.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "StreamZH.Secrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

public static class SecretProtectionExtensions
{
    /// <summary>
    /// At start, before anything reads a secret: encrypt from now on, encrypt what is still plain (older versions,
    /// restored backups), create the API key at the first start, and warn about secrets that cannot be decrypted.
    /// </summary>
    public static void UseStreamZhSecrets(this WebApplication app, Database database, ConfigRepository config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(config);
        database.UseSecretProtector(new DataProtectionSecretProtector(app.Services.GetRequiredService<IDataProtectionProvider>()));

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StreamZH.Secrets");
        var encrypted = SecretMigration.EncryptPlaintext(database);
        if (encrypted > 0)
        {
            logger.LogInformation("Encrypted {Count} stored passwords, API keys and tokens", encrypted);
        }

        config.GetOrCreate(ConfigKeys.ApiKey, GeneralSettingsEndpoints.NewApiKey);

        var unreadable = SecretMigration.FindUnreadable(database);
        if (unreadable.Count > 0)
        {
            logger.LogWarning(
                "{Count} stored secrets cannot be decrypted (the key folder {Keys} is missing or from another installation); enter them again: {Items}",
                unreadable.Count,
                Path.Combine(app.Services.GetRequiredService<StreamZH.Core.AppPaths>().DataDirectory, "keys"),
                string.Join(", ", unreadable.Select(l => $"{l.Kind} {l.Item} ({l.Field})")));
        }
    }
}
