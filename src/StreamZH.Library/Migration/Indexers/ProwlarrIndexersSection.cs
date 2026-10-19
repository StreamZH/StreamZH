// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Library.Indexers;

namespace StreamZH.Library.Migration.Indexers;

/// <summary>
/// Prowlarr's indexers → StreamZH indexers, through the existing <see cref="ProwlarrImportService"/> (same rules as
/// Indexers → Import from Prowlarr). Prowlarr masks the indexers' own API keys, so they are reached through Prowlarr's
/// Newznab proxy unless the user types the indexer's own key (<see cref="MigrationContext.Secret"/>): then the indexer
/// is used directly and keeps working once Prowlarr is gone. An indexer with the same name is kept as it is.
/// </summary>
public sealed class ProwlarrIndexersSection : IMigrationSection
{
    public const string SectionKey = "indexers";

    public string Source => MigrationSources.Prowlarr;

    public string Key => SectionKey;

    public string Label => "Indexers";

    public string Description => "Usenet indexers with their categories, priority and limits. Type an indexer's own API key to use it without Prowlarr.";

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
        if (preview.Any(p => p.Supported && !p.AlreadyExists && p.Mode == ProwlarrImportMode.Proxy && context.Secret(ItemKey(p.ProwlarrId)) is null))
        {
            warnings.Add("Indexers without their own API key go through Prowlarr: keep Prowlarr running, or type the indexers' own API keys so StreamZH reaches them directly.");
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

            if (created.Mode == ProwlarrImportMode.Proxy)
            {
                run.Info($"{created.Name} goes through Prowlarr. Enter its own API key in Indexers before you stop Prowlarr.", created.Name);
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
        var catalog = IndexerCatalog.Match(p.Name, p.OwnUrl);
        var known = catalog.Key != IndexerCatalog.GenericNewznabKey ? $"{catalog.Name} · " : string.Empty;
        if (!p.Supported)
        {
            return new MigrationItem(key, p.Name, MigrationAction.Skip, $"{p.Protocol} · {p.Implementation}", p.Reason);
        }

        if (p.AlreadyExists)
        {
            return new MigrationItem(key, p.Name, MigrationAction.Link, $"{known}already in StreamZH, kept as it is");
        }

        var direct = p.Mode == ProwlarrImportMode.Direct || (p.OwnUrl is not null && context.Secret(key) is not null);
        var detail = direct
            ? $"{known}direct: {p.OwnUrl ?? p.BaseUrl + p.ApiPath}"
            : $"{known}through Prowlarr ({p.BaseUrl}{p.ApiPath})";

        // Only an indexer whose own address Prowlarr shows can be used directly with a typed-in key.
        var canUseOwnKey = p.Mode == ProwlarrImportMode.Proxy && p.OwnUrl is not null;
        return new MigrationItem(
            key,
            p.Name,
            MigrationAction.Create,
            detail,
            Warning: canUseOwnKey && !direct ? "Prowlarr hides the indexer's own API key. Type it to use the indexer without Prowlarr." : null,
            NeedsSecret: canUseOwnKey);
    }
}
