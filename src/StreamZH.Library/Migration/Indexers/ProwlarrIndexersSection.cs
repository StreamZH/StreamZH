// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Library.Indexers;

namespace StreamZH.Library.Migration.Indexers;

/// <summary>
/// Prowlarr's indexers (Usenet and, since 0.11, torrent) → StreamZH indexers, through the existing <see cref="ProwlarrImportService"/>
/// (same rules as Indexers → Import from Prowlarr). An indexer with an API of its own always gets the provider's address, never
/// Prowlarr's proxy. Prowlarr masks the indexers' API keys: the user types the key (<see cref="MigrationContext.Secret"/>), or the
/// indexer is added switched off until the key is entered in Indexers. Only scrapers without an API go through Prowlarr.
/// An indexer with the same name is kept as it is.
/// </summary>
public sealed class ProwlarrIndexersSection : IMigrationSection
{
    public const string SectionKey = "indexers";

    public string Source => MigrationSources.Prowlarr;

    public string Key => SectionKey;

    public string Label => "Indexers";

    public string Description => "Usenet and torrent indexers with their own address, categories, priority, limits and seed settings. Type each indexer's API key; Prowlarr hides it.";

    public int Order => 350;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public static string ItemKey(int prowlarrId) => string.Create(CultureInfo.InvariantCulture, $"{SectionKey}:{prowlarrId}");

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var import = context.Services.GetRequiredService<ProwlarrImportService>();
        var preview = await import.PreviewAsync(context.Connection.Url, context.Connection.ApiKey, cancellationToken);
        var items = preview.Select(p => Item(p, context)).ToList();
        var warnings = new List<string>();
        if (preview.Any(p => p.Supported && !p.AlreadyExists && p.Mode == ProwlarrImportMode.NeedsKey && context.Secret(ItemKey(p.ProwlarrId)) is null))
        {
            warnings.Add("Prowlarr hides the indexers' API keys. Indexers without a typed key are added switched off: enter the key in Indexers → Edit, then switch them on.");
        }

        if (preview.Any(p => p.Supported && !p.AlreadyExists && p.Mode == ProwlarrImportMode.Proxy))
        {
            warnings.Add("Indexers without an API of their own (site scrapers) can only be searched through Prowlarr: keep Prowlarr running for them.");
        }

        return new MigrationSectionPlan(items, warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var import = context.Services.GetRequiredService<ProwlarrImportService>();
        var preview = await import.PreviewAsync(context.Connection.Url, context.Connection.ApiKey, cancellationToken);
        var wanted = new List<int>();
        var ownKeys = new Dictionary<int, string>();
        foreach (var indexer in preview)
        {
            var key = ItemKey(indexer.ProwlarrId);
            if (context.IsExcluded(key))
            {
                run.Skipped(indexer.Name, "Left out in the wizard.");
            }
            else if (!indexer.Supported)
            {
                run.Skipped(indexer.Name, indexer.Reason ?? "Not supported.");
            }
            else if (indexer.AlreadyExists)
            {
                run.Linked(indexer.Name);
            }
            else
            {
                wanted.Add(indexer.ProwlarrId);
                if (context.Secret(key) is { } own)
                {
                    ownKeys[indexer.ProwlarrId] = own;
                }
            }
        }

        run.Progress(0, Math.Max(1, wanted.Count), "Creating indexers");
        if (wanted.Count == 0)
        {
            return;
        }

        var result = await import.ImportAsync(context.Connection.Url, context.Connection.ApiKey, wanted, cancellationToken, ownKeys);
        var byName = preview.ToLookup(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var created in result.Imported)
        {
            run.Created(created.Name);
            if (byName[created.Name].FirstOrDefault() is { } source)
            {
                context.Ids.Set("indexer", source.ProwlarrId, created.Id);
            }

            if (created.Mode == ProwlarrImportMode.NeedsKey)
            {
                run.Info($"{created.Name} is switched off until you enter its API key in Indexers → Edit.", created.Name);
            }
            else if (created.Mode == ProwlarrImportMode.Proxy)
            {
                run.Info($"{created.Name} has no API of its own and goes through Prowlarr.", created.Name);
            }
        }

        foreach (var skipped in result.Skipped)
        {
            run.Skipped(skipped.Name.Length > 0 ? skipped.Name : ItemKey(skipped.ProwlarrId), skipped.Reason);
        }

        run.Progress(wanted.Count, wanted.Count);
    }

    private static MigrationItem Item(ProwlarrIndexerPreview p, MigrationContext context)
    {
        var key = ItemKey(p.ProwlarrId);
        var catalog = IndexerCatalog.Match(p.Name, p.OwnUrl, p.Protocol == "torrent" ? IndexerProtocol.Torrent : IndexerProtocol.Usenet);
        var known = !IndexerCatalog.IsGeneric(catalog.Key) ? $"{catalog.Name} · " : string.Empty;
        if (!p.Supported)
        {
            return new MigrationItem(key, p.Name, MigrationAction.Skip, $"{p.Protocol} · {p.Implementation}", p.Reason);
        }

        if (p.AlreadyExists)
        {
            return new MigrationItem(key, p.Name, MigrationAction.Link, $"{known}already in StreamZH, kept as it is");
        }

        var needsKey = p.Mode == ProwlarrImportMode.NeedsKey;
        var detail = p.Mode == ProwlarrImportMode.Proxy
            ? $"{known}through Prowlarr ({p.BaseUrl}{p.ApiPath}), no API of its own"
            : $"{known}{p.OwnUrl ?? p.BaseUrl + p.ApiPath}";

        return new MigrationItem(
            key,
            p.Name,
            MigrationAction.Create,
            detail,
            Warning: needsKey && context.Secret(key) is null ? "Prowlarr hides the API key. Type it, or the indexer is added switched off." : null,
            NeedsSecret: needsKey);
    }
}
