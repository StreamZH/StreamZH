// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Net;
using StreamZH.Data;
using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Library.Indexers;

/// <summary>
/// 0.12 (V-10): the hosts of every configured indexer and download client. A feed link or redirect to one of them may
/// reach it on the local network (e.g. a Prowlarr link to Jackett, a link to the download client); links to other LAN
/// hosts are refused. Read from the database at most every <see cref="Lifetime"/>.
/// </summary>
public sealed class ConfiguredServiceHosts(IndexerRepository indexers, DownloadClientRepository clients, TimeProvider time) : IConfiguredServiceHosts
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private (HashSet<string> Hosts, DateTimeOffset Until)? _cache;

    /// <summary>0.12 (SV2 N-07): host and port must both match a configured indexer or download client.</summary>
    public bool Contains(string host, int port) =>
        !string.IsNullOrWhiteSpace(host) && Load().Contains(Key(host, port));

    private static string Key(string host, int port) =>
        $"{host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant()}|{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private HashSet<string> Load()
    {
        var now = time.GetUtcNow();
        if (_cache is { } cached && cached.Until > now)
        {
            return cached.Hosts;
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var indexer in indexers.All())
        {
            if (Uri.TryCreate(indexer.BaseUrl, UriKind.Absolute, out var url))
            {
                hosts.Add(Key(url.IdnHost, url.Port));
            }
        }

        foreach (var client in clients.All())
        {
            if (client.Settings.TryGetValue(DownloadClientCatalog.Host, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } host && Port(client) is { } port)
            {
                hosts.Add(Key(host, port));
            }
        }

        _cache = (hosts, now + Lifetime);
        return hosts;
    }

    // The client's port setting, else the default port of its implementation.
    private static int? Port(StreamZH.Core.Downloads.DownloadClientDefinition client)
    {
        if (client.Settings.TryGetValue(DownloadClientCatalog.Port, out var stored) && stored.ValueKind == JsonValueKind.Number && stored.TryGetInt32(out var port))
        {
            return port;
        }

        return DownloadClientCatalog.Find(client.Implementation)?.Field(DownloadClientCatalog.Port) is { Default: { ValueKind: JsonValueKind.Number } d } && d.TryGetInt32(out var fallback)
            ? fallback
            : null;
    }
}
