// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Host.Auth;
using StreamZH.Torrent;

namespace StreamZH.Host.Api;

/// <summary>
/// The built-in torrent engine (0.11): its settings (Settings → Download clients → StreamZH), its status and its
/// torrents. Reads for members, changes for admins.
/// </summary>
public static class TorrentEngineEndpoints
{
    public static IEndpointRouteBuilder MapTorrentEngineEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings/torrentengine").WithTags("Settings").RequireAuthorization();

        settings.MapGet("/", (TorrentEngineSettings engineSettings) => TorrentEngineSettingsResource.From(engineSettings.Current));

        // The body is the resource as GET returns it; fields left out keep their value. seedRatio and
        // seedTimeMinutes: null = no goal.
        settings.MapPut("/", async (JsonElement body, TorrentEngineSettings engineSettings, IEventBus events, ILogger<TorrentEngineSettings> logger, CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [string.Empty] = ["Send the settings as a JSON object."] });
            }

            var (options, errors) = TorrentEngineSettingsResource.Apply(engineSettings.Current, body);
            foreach (var (field, messages) in options.Validate())
            {
                errors.TryAdd(field, messages);
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            engineSettings.Save(options);
            await events.PublishAsync(new OngoingChanged(), ct);
            logger.LogInformation(
                "Torrent engine settings saved: port {Port}, DHT {Dht}, PEX {Pex}, local peers {Lpd}, encryption {Encryption}, port forwarding {Forwarding}",
                options.ListenPort,
                options.Dht,
                options.Pex,
                options.LocalPeerDiscovery,
                TorrentEngineSettings.EncryptionName(options.Encryption),
                options.PortForwarding);
            return Results.Ok(TorrentEngineSettingsResource.From(engineSettings.Current));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        var engine = app.MapGroup("/api/v1/torrentengine").WithTags("Torrent engine").RequireAuthorization();

        engine.MapGet("/status", (TorrentEngine torrentEngine) => torrentEngine.Status());

        engine.MapGet("/torrents", async (string? category, TorrentEngine torrentEngine, CancellationToken ct) =>
            (await torrentEngine.GetTorrentsAsync(string.IsNullOrWhiteSpace(category) ? null : category.Trim(), ct)).Select(TorrentEngineTorrentResource.From).ToList());

        engine.MapPost("/torrents/{hash}/pause", async (string hash, TorrentEngine torrentEngine, CancellationToken ct) =>
            TorrentStateStore.IsHash(hash.ToLowerInvariant()) && await torrentEngine.PauseAsync(hash, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        engine.MapPost("/torrents/{hash}/resume", async (string hash, TorrentEngine torrentEngine, CancellationToken ct) =>
            TorrentStateStore.IsHash(hash.ToLowerInvariant()) && await torrentEngine.ResumeAsync(hash, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        engine.MapDelete("/torrents/{hash}", async (string hash, bool? deleteData, TorrentEngine torrentEngine, CancellationToken ct) =>
            TorrentStateStore.IsHash(hash.ToLowerInvariant()) && await torrentEngine.RemoveAsync(hash, deleteData == true, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }
}

/// <param name="ListenPort">TCP port for incoming peers; DHT uses the same number on UDP.</param>
/// <param name="BindAddress">"" = all interfaces, else the only IP address the engine uses.</param>
/// <param name="DownloadLimitKBps">0 = unlimited.</param>
/// <param name="UploadLimitKBps">0 = unlimited.</param>
/// <param name="Encryption">"prefer", "require" or "allow".</param>
/// <param name="PortForwarding">UPnP / NAT-PMP on the router (off by default).</param>
/// <param name="SeedRatio">Default ratio goal; null = none. A torrent's own goal (from its indexer) wins.</param>
/// <param name="SeedTimeMinutes">Default seeding time goal; null = none.</param>
public sealed record TorrentEngineSettingsResource(
    int ListenPort,
    string BindAddress,
    int MaxConnections,
    int MaxConnectionsPerTorrent,
    long DownloadLimitKBps,
    long UploadLimitKBps,
    bool Dht,
    bool Pex,
    bool LocalPeerDiscovery,
    string Encryption,
    bool PortForwarding,
    double? SeedRatio,
    int? SeedTimeMinutes)
{
    public static TorrentEngineSettingsResource From(TorrentEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TorrentEngineSettingsResource(
            options.ListenPort,
            options.BindAddress,
            options.MaxConnections,
            options.MaxConnectionsPerTorrent,
            options.DownloadLimitKBps,
            options.UploadLimitKBps,
            options.Dht,
            options.Pex,
            options.LocalPeerDiscovery,
            TorrentEngineSettings.EncryptionName(options.Encryption),
            options.PortForwarding,
            options.SeedRatio,
            options.SeedTimeMinutes);
    }

    /// <summary>The fields present in <paramref name="body"/> applied to <paramref name="current"/>; wrong types become field errors.</summary>
    public static (TorrentEngineOptions Options, Dictionary<string, string[]> Errors) Apply(TorrentEngineOptions current, JsonElement body)
    {
        ArgumentNullException.ThrowIfNull(current);
        var errors = new Dictionary<string, string[]>();
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in body.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        int Int(string name, int fallback) => Read(name, fallback, e => e.TryGetInt32(out var v) ? v : null, "Enter a whole number.");
        long Long(string name, long fallback) => Read(name, fallback, e => e.TryGetInt64(out var v) ? v : null, "Enter a whole number.");
        bool Bool(string name, bool fallback) => Read<bool>(name, fallback, e => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null, "Enter true or false.");

        T Read<T>(string name, T fallback, Func<JsonElement, T?> parse, string message)
            where T : struct
        {
            if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return fallback;
            }

            if ((typeof(T) == typeof(bool) || value.ValueKind == JsonValueKind.Number) && parse(value) is { } parsed)
            {
                return parsed;
            }

            errors[name] = [message];
            return fallback;
        }

        var bindAddress = current.BindAddress;
        if (fields.TryGetValue("bindAddress", out var bind) && bind.ValueKind != JsonValueKind.Null)
        {
            if (bind.ValueKind == JsonValueKind.String)
            {
                bindAddress = bind.GetString()!.Trim();
            }
            else
            {
                errors["bindAddress"] = ["Enter an IP address, or leave it empty."];
            }
        }

        var encryption = current.Encryption;
        if (fields.TryGetValue("encryption", out var mode) && mode.ValueKind != JsonValueKind.Null)
        {
            if (mode.ValueKind == JsonValueKind.String && TorrentEngineSettings.ParseEncryption(mode.GetString()) is { } parsed)
            {
                encryption = parsed;
            }
            else
            {
                errors["encryption"] = ["Choose prefer, require or allow."];
            }
        }

        var seedRatio = current.SeedRatio;
        if (fields.TryGetValue("seedRatio", out var ratio))
        {
            if (ratio.ValueKind == JsonValueKind.Null)
            {
                seedRatio = null;
            }
            else if (ratio.ValueKind == JsonValueKind.Number && ratio.TryGetDouble(out var value))
            {
                seedRatio = value;
            }
            else
            {
                errors["seedRatio"] = ["Enter a ratio like 1 or 1.5, or leave it empty."];
            }
        }

        var seedTime = current.SeedTimeMinutes;
        if (fields.TryGetValue("seedTimeMinutes", out var time))
        {
            if (time.ValueKind == JsonValueKind.Null)
            {
                seedTime = null;
            }
            else if (time.ValueKind == JsonValueKind.Number && time.TryGetInt32(out var value))
            {
                seedTime = value;
            }
            else
            {
                errors["seedTimeMinutes"] = ["Enter minutes as a whole number, or leave it empty."];
            }
        }

        var options = new TorrentEngineOptions(
            Int("listenPort", current.ListenPort),
            bindAddress,
            Int("maxConnections", current.MaxConnections),
            Int("maxConnectionsPerTorrent", current.MaxConnectionsPerTorrent),
            Long("downloadLimitKBps", current.DownloadLimitKBps),
            Long("uploadLimitKBps", current.UploadLimitKBps),
            Bool("dht", current.Dht),
            Bool("pex", current.Pex),
            Bool("localPeerDiscovery", current.LocalPeerDiscovery),
            encryption,
            Bool("portForwarding", current.PortForwarding),
            seedRatio,
            seedTime);
        return (options, errors);
    }
}

/// <summary>One torrent of the built-in engine.</summary>
/// <param name="State">queued, checking, downloading, stalled, paused, seeding, completed or error.</param>
/// <param name="Progress">0..1.</param>
public sealed record TorrentEngineTorrentResource(
    string Hash,
    string Name,
    string State,
    long Size,
    long Downloaded,
    long Uploaded,
    double Ratio,
    double Progress,
    long DownloadSpeed,
    long UploadSpeed,
    double? EtaSeconds,
    int Seeders,
    int Peers,
    string? Category,
    string? SavePath,
    string? ContentPath,
    string? Error,
    DateTimeOffset? Added,
    DateTimeOffset? CompletedOn,
    double SeedingTimeSeconds)
{
    public static TorrentEngineTorrentResource From(TorrentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new TorrentEngineTorrentResource(
            status.Hash,
            status.Name,
            JsonNamingPolicy.CamelCase.ConvertName(status.State.ToString()),
            status.Size,
            status.Downloaded,
            status.Uploaded,
            status.Ratio,
            Math.Round(status.Progress, 4),
            status.DownloadSpeed,
            status.UploadSpeed,
            status.Eta?.TotalSeconds,
            status.Seeders,
            status.Peers,
            status.Category,
            status.SavePath,
            status.ContentPath,
            status.Error,
            status.Added,
            status.CompletedOn,
            Math.Round(status.SeedingTime.TotalSeconds));
    }
}
