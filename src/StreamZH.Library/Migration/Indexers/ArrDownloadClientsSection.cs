// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace StreamZH.Library.Migration.Indexers;

/// <summary>
/// The download clients of Radarr or Sonarr, for information only: StreamZH downloads by itself, so nothing is created.
/// For a SABnzbd client the item's <see cref="MigrationItem.Detail"/> is SABnzbd's address (e.g. "http://192.168.1.10:8080")
/// so the wizard can offer to take over SABnzbd with it; the label names the category Radarr/Sonarr use.
/// </summary>
public sealed class ArrDownloadClientsSection(string source) : IMigrationSection
{
    public const string SectionKey = "downloadClients";

    public const string TakeOverSabnzbd = "StreamZH downloads by itself. Take over SABnzbd's servers and categories from SABnzbd (Settings → Migration → SABnzbd).";

    public string Source { get; } = source;

    public string Key => SectionKey;

    public string Label => "Download clients";

    public string Description => "Shows which downloader is used. Nothing is created: StreamZH downloads by itself; take over SABnzbd itself instead.";

    public int Order => 361;

    public bool DefaultSelected => false;

    public bool Advanced => true;

    public static string ItemKey(int id) => string.Create(CultureInfo.InvariantCulture, $"{SectionKey}:{id}");

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken) =>
        new(await ItemsAsync(context, cancellationToken));

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        foreach (var item in await ItemsAsync(context, cancellationToken))
        {
            run.Skipped(item.Label, item.Reason ?? TakeOverSabnzbd);
        }
    }

    private async Task<List<MigrationItem>> ItemsAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var client = context.Services.GetRequiredService<ArrApiClient>();
        using var document = await client.GetJsonAsync(Source, context.Connection, "/downloadclient", cancellationToken);
        var items = new List<MigrationItem>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                continue;
            }

            var fields = Fields(entry);
            var name = Text(entry, "name") ?? $"Download client {id.ToString(CultureInfo.InvariantCulture)}";
            var implementation = Text(entry, "implementation") ?? string.Empty;
            var enabled = !entry.TryGetProperty("enable", out var enable) || enable.ValueKind != JsonValueKind.False;
            if (string.Equals(implementation, "Sabnzbd", StringComparison.OrdinalIgnoreCase))
            {
                var host = Field(fields, "host") ?? "localhost";
                var port = Field(fields, "port") ?? "8080";
                var ssl = Field(fields, "useSsl") is "true" or "True" or "1";
                var urlBase = (Field(fields, "urlBase") ?? string.Empty).Trim().Trim('/');
                var category = Field(fields, Source == MigrationSources.Sonarr ? "tvCategory" : "movieCategory");
                var url = $"{(ssl ? "https" : "http")}://{host}:{port}{(urlBase.Length > 0 ? "/" + urlBase : string.Empty)}";
                var label = $"SABnzbd at {host}:{port}{(string.IsNullOrWhiteSpace(category) ? string.Empty : $", category {category}")}{(enabled ? string.Empty : " (switched off)")}";
                items.Add(new MigrationItem(ItemKey(id), label, MigrationAction.Skip, url, TakeOverSabnzbd));
            }
            else
            {
                var protocol = Text(entry, "protocol") ?? string.Empty;
                items.Add(new MigrationItem(
                    ItemKey(id),
                    $"{name} ({implementation})",
                    MigrationAction.Skip,
                    null,
                    protocol == "torrent"
                        ? "Torrent clients follow with torrent support in StreamZH 1.0."
                        : "StreamZH downloads by itself with its Usenet servers (Settings → Usenet)."));
            }
        }

        return items;
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement entry)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in list.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    fields[name.GetString()!] = field.TryGetProperty("value", out var value) ? value.Clone() : default;
                }
            }
        }

        return fields;
    }

    private static string? Field(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
