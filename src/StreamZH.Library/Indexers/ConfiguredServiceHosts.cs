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

    public bool Contains(string host) => !string.IsNullOrWhiteSpace(host) && Load().Contains(host.Trim().Trim('[', ']').TrimEnd('.'));

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
                hosts.Add(url.IdnHost.Trim('[', ']'));
            }
        }

        foreach (var client in clients.All())
        {
            if (client.Settings.TryGetValue(DownloadClientCatalog.Host, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } host)
            {
                hosts.Add(host.Trim().Trim('[', ']').TrimEnd('.'));
            }
        }

        _cache = (hosts, now + Lifetime);
        return hosts;
    }
}
