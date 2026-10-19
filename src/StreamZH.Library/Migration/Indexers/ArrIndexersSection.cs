// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Indexers;
using StreamZH.Library.Indexers;

namespace StreamZH.Library.Migration.Indexers;

/// <summary>One indexer of Radarr or Sonarr (GET /api/v3/indexer, IndexerResource with its provider fields).</summary>
public sealed record ArrIndexer(
    int Id,
    string Name,
    string Implementation,
    string Protocol,
    bool EnableRss,
    bool EnableAutomaticSearch,
    bool EnableInteractiveSearch,
    int Priority,
    IReadOnlyDictionary<string, JsonElement> Fields)
{
    /// <summary>Radarr/Sonarr's placeholder for API keys and passwords (SchemaBuilder.PRIVATE_VALUE).</summary>
    public const string Masked = "********";

    public string Text(string field) =>
        Fields.TryGetValue(field, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        } : string.Empty;

    /// <summary>A number field (Torznab seed settings); null when empty or not a number.</summary>
    public double? Number(string field) =>
        Fields.TryGetValue(field, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var n) => n,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        } : null;

    public bool IsTorrent => Protocol == "torrent";

    public IReadOnlyList<int> Numbers(string field) =>
        Fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0).Where(n => n > 0)]
            : [];

    public static IReadOnlyList<ArrIndexer> Parse(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ArrIndexer>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                continue;
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("fields", out var fieldList) && fieldList.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldList.EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        fields[name.GetString()!] = field.TryGetProperty("value", out var value) ? value.Clone() : default;
                    }
                }
            }

            result.Add(new ArrIndexer(
                id,
                Str(item, "name") ?? string.Create(CultureInfo.InvariantCulture, $"Indexer {id}"),
                Str(item, "implementation") ?? string.Empty,
                ProtocolOf(item),
                Bool(item, "enableRss") ?? true,
                Bool(item, "enableAutomaticSearch") ?? true,
                Bool(item, "enableInteractiveSearch") ?? true,
                item.TryGetProperty("priority", out var p) && p.TryGetInt32(out var priority) ? priority : IndexerService.DefaultPriority,
                fields));
        }

        return result;
    }

    // v3 writes "usenet"/"torrent"; older versions the enum number (1 usenet, 2 torrent).
    private static string ProtocolOf(JsonElement item)
    {
        if (!item.TryGetProperty("protocol", out var protocol))
        {
            return "unknown";
        }

        return protocol.ValueKind switch
        {
            JsonValueKind.String => protocol.GetString()?.Trim().ToLowerInvariant() ?? "unknown",
            JsonValueKind.Number when protocol.TryGetInt32(out var n) => n switch { 1 => "usenet", 2 => "torrent", _ => "unknown" },
            _ => "unknown",
        };
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}

/// <summary>
/// The Newznab and (0.11) Torznab indexers of Radarr or Sonarr → StreamZH indexers. One StreamZH indexer serves movies and
/// TV: the same indexer in Radarr and Sonarr (same base URL) becomes one indexer — Radarr's categories as movie
/// categories, Sonarr's (and its anime categories) as TV categories, whichever app is taken over first. Existing StreamZH
/// indexers are matched by base URL and updated. Torznab indexers bring their minimum seeders and seed criteria (seed
/// ratio, seed time, Sonarr's season pack seed time). Indexers that come from Prowlarr are skipped. API keys are masked
/// by the apps; without a typed-in key a new indexer is added switched off.
/// </summary>
public sealed partial class ArrIndexersSection(string source) : IMigrationSection
{
    public const string SectionKey = "indexers";

    public string Source { get; } = source;

    public string Key => SectionKey;

    public string Label => "Indexers";

    public string Description => Source == MigrationSources.Sonarr
        ? "Newznab and Torznab indexers with their TV and anime categories, priority and seed settings. The same indexer from Radarr becomes one indexer."
        : "Newznab and Torznab indexers with their movie categories, priority and seed settings. The same indexer from Sonarr becomes one indexer.";

    public int Order => 350;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    private bool IsSonarr => Source == MigrationSources.Sonarr;

    private string App => ArrApiClient.Name(Source);

    public static string ItemKey(int id) => string.Create(CultureInfo.InvariantCulture, $"{SectionKey}:{id}");

    /// <summary>Lower-case scheme and host, no trailing slash: "HTTPS://API.nzbgeek.info/" → "https://api.nzbgeek.info".</summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? (uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/')).TrimEnd('/')
            : trimmed.ToLowerInvariant();
    }

    /// <summary>Prowlarr's app sync adds indexers as "http://prowlarr:9696/1/" with the API path "/api".</summary>
    public static bool ComesFromProwlarr(ArrIndexer indexer)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        var url = indexer.Text("baseUrl");
        return indexer.Name.EndsWith("(Prowlarr)", StringComparison.OrdinalIgnoreCase)
            || (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && ProwlarrPathRegex().IsMatch(uri.AbsolutePath)
                && (uri.Port == 9696 || uri.Host.Contains("prowlarr", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(context, cancellationToken);
        var warnings = new List<string>();
        if (plan.Any(p => p.Item.NeedsSecret))
        {
            warnings.Add($"{App} does not hand out the indexers' API keys. Type them in; an indexer without a key is added switched off.");
        }

        if (plan.Any(p => p.FromProwlarr))
        {
            warnings.Add($"Some indexers come from Prowlarr. Take them over from Prowlarr instead (Settings → Migration → Prowlarr).");
        }

        return new MigrationSectionPlan([.. plan.Select(p => p.Item)], warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var service = context.Services.GetRequiredService<IndexerService>();
        var plan = await PlanAsync(context, cancellationToken);
        var done = 0;
        foreach (var step in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(done++, plan.Count, step.Item.Label);
            if (step.Item.Action == MigrationAction.Skip || step.Remote is null)
            {
                run.Skipped(step.Item.Label, step.Item.Reason ?? "Not taken over.");
                continue;
            }

            if (context.IsExcluded(step.Item.Key))
            {
                run.Skipped(step.Item.Label, "Left out in the wizard.");
                continue;
            }

            try
            {
                var saved = Save(service, step.Remote, step.Existing is null ? null : service.Find(step.Existing.Id), context.Secret(step.Item.Key), run);
                context.Ids.Set("indexer", step.Remote.Id, saved.Id);
            }
            catch (ValidationFailedException ex)
            {
                run.Failed(step.Item.Label, string.Join(" ", ex.Errors.Values.SelectMany(v => v)));
            }
            catch (LibraryException ex)
            {
                run.Failed(step.Item.Label, ex.Message);
            }
        }

        run.Progress(plan.Count, plan.Count);
    }

    private sealed record Step(MigrationItem Item, ArrIndexer? Remote = null, IndexerDefinition? Existing = null, bool FromProwlarr = false);

    private async Task<List<Step>> PlanAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var client = context.Services.GetRequiredService<ArrApiClient>();
        var service = context.Services.GetRequiredService<IndexerService>();
        using var document = await client.GetJsonAsync(Source, context.Connection, "/indexer", cancellationToken);
        var remote = ArrIndexer.Parse(document.RootElement);
        var existing = service.All();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<Step>();
        foreach (var indexer in remote)
        {
            var key = ItemKey(indexer.Id);
            var baseUrl = indexer.Text("baseUrl").Trim();
            var wantedImplementation = indexer.IsTorrent ? IndexerDefinition.Torznab : IndexerDefinition.Newznab;
            if (!string.Equals(indexer.Implementation, wantedImplementation, StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new Step(new MigrationItem(key, indexer.Name, MigrationAction.Skip, indexer.Implementation,
                    "Only Newznab and Torznab indexers can be taken over; add this one in Indexers if StreamZH's catalog has it.")));
                continue;
            }

            if (ComesFromProwlarr(indexer))
            {
                steps.Add(new Step(
                    new MigrationItem(key, indexer.Name, MigrationAction.Skip, baseUrl, "Comes from Prowlarr: take it over from Prowlarr instead (Settings → Migration → Prowlarr)."),
                    FromProwlarr: true));
                continue;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                steps.Add(new Step(new MigrationItem(key, indexer.Name, MigrationAction.Skip, baseUrl, "No usable URL.")));
                continue;
            }

            var normalized = NormalizeUrl(baseUrl);
            if (seen.TryGetValue(normalized, out var first))
            {
                steps.Add(new Step(new MigrationItem(key, indexer.Name, MigrationAction.Skip, baseUrl, $"Same indexer as \"{first}\" in {App}.")));
                continue;
            }

            seen[normalized] = indexer.Name;
            var match = existing.FirstOrDefault(e => NormalizeUrl(e.BaseUrl) == normalized);
            var masked = indexer.Text("apiKey") == ArrIndexer.Masked;
            var needsKey = masked && (match is null || match.ApiKey.Length == 0) && context.Secret(key) is null;
            var catalog = IndexerCatalog.Match(indexer.Name, baseUrl, ProtocolOf(indexer));
            var categories = Categories(indexer);
            var parts = new List<string>
            {
                !IndexerCatalog.IsGeneric(catalog.Key) ? $"{catalog.Name} · {baseUrl}" : baseUrl,
                $"{(IsSonarr ? "TV" : "movie")} categories {(categories.Count == 0 ? "default" : string.Join(", ", categories))}",
                string.Create(CultureInfo.InvariantCulture, $"priority {indexer.Priority}"),
            };
            if (indexer.IsTorrent)
            {
                parts.Insert(0, "torrent");
                if (SeedSummary(indexer) is { } seed)
                {
                    parts.Add(seed);
                }
            }
            if (match is not null)
            {
                parts.Add(match.Name.Equals(indexer.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"adds the {(IsSonarr ? "TV" : "movie")} categories to the existing indexer"
                    : $"adds the {(IsSonarr ? "TV" : "movie")} categories to \"{match.Name}\"");
            }

            steps.Add(new Step(
                new MigrationItem(
                    key,
                    indexer.Name,
                    match is null ? MigrationAction.Create : MigrationAction.Update,
                    string.Join(" · ", parts),
                    Warning: needsKey ? "Type the API key; without it the indexer is added switched off." : null,
                    NeedsSecret: masked && (match is null || match.ApiKey.Length == 0)),
                indexer,
                match));
        }

        return steps;
    }

    /// <summary>Radarr: categories; Sonarr: categories plus anime categories (StreamZH has one TV list).</summary>
    private List<int> Categories(ArrIndexer indexer) =>
        [.. (IsSonarr ? indexer.Numbers("categories").Concat(indexer.Numbers("animeCategories")) : indexer.Numbers("categories")).Distinct().Order()];

    private IndexerDefinition Save(IndexerService service, ArrIndexer remote, IndexerDefinition? existing, string? typedKey, MigrationSectionRun run)
    {
        var baseUrl = remote.Text("baseUrl").Trim().TrimEnd('/');
        var apiPath = remote.Text("apiPath") is { Length: > 0 } path ? path.Trim() : IndexerService.DefaultApiPath;
        var remoteKey = remote.Text("apiKey");
        var key = typedKey?.Trim() is { Length: > 0 } typed ? typed : remoteKey == ArrIndexer.Masked ? null : remoteKey.Trim();
        var categories = Categories(remote);
        var priority = Math.Clamp(remote.Priority, IndexerService.MinPriority, IndexerService.MaxPriority);
        var protocol = ProtocolOf(remote);
        var catalog = IndexerCatalog.Match(remote.Name, baseUrl, protocol);
        var definitionKey = catalog.Supported ? catalog.Key : IndexerCatalog.GenericKey(protocol);
        var additional = remote.Text("additionalParameters").Trim();
        var implementation = remote.IsTorrent ? IndexerDefinition.Torznab : IndexerDefinition.Newznab;
        var seed = SeedSettings(remote);

        if (existing is null)
        {
            // The other app's categories stay empty until it is taken over; without any, the defaults.
            var movies = IsSonarr ? [] : categories.Count > 0 ? categories : [.. IndexerService.DefaultMovieCategories];
            var tv = IsSonarr ? categories.Count > 0 ? categories : [.. IndexerService.DefaultTvCategories] : new List<int>();
            var enabled = key is not null;
            var created = service.Create(new IndexerDefinition(
                0, UniqueName(service, remote.Name), implementation, baseUrl, apiPath, key ?? string.Empty, movies, tv, priority,
                remote.EnableRss, remote.EnableAutomaticSearch, remote.EnableInteractiveSearch)
            {
                Enable = enabled,
                DefinitionKey = definitionKey,
                AdditionalParameters = additional,
                MinimumSeeders = seed.MinimumSeeders,
                SeedRatio = seed.Ratio,
                SeedTimeMinutes = seed.Time,
                SeasonPackSeedTimeMinutes = seed.PackTime,
            });
            run.Created(remote.Name);
            if (!enabled)
            {
                run.Warn($"{created.Name}: no API key, so it was added switched off. Enter the key in Indexers and switch it on.", remote.Name);
            }

            return created;
        }

        // The same indexer from the other app: keep its categories for the other kind, take ours; switches add up.
        var hasKey = key is not null || existing.ApiKey.Length > 0;
        var updated = service.Update(existing.Id, existing with
        {
            BaseUrl = baseUrl,
            ApiPath = apiPath,
            ApiKey = key ?? IndexerService.MaskedApiKey,
            Categories = IsSonarr ? existing.Categories : categories.Count > 0 ? categories : existing.Categories,
            TvCategories = IsSonarr ? categories.Count > 0 ? categories : existing.TvCategories : existing.TvCategories,
            Priority = priority,
            EnableRss = existing.EnableRss || remote.EnableRss,
            EnableAutomaticSearch = existing.EnableAutomaticSearch || remote.EnableAutomaticSearch,
            EnableInteractiveSearch = existing.EnableInteractiveSearch || remote.EnableInteractiveSearch,

            // An indexer that was added without a key is switched on once the key is there.
            Enable = existing.Enable || (existing.ApiKey.Length == 0 && key is not null),
            DefinitionKey = existing.DefinitionKey ?? definitionKey,
            AdditionalParameters = existing.AdditionalParameters.Length > 0 ? existing.AdditionalParameters : additional,

            // Seed settings: what the first app set stays, the second app fills in what is empty.
            SeedRatio = existing.SeedRatio ?? seed.Ratio,
            SeedTimeMinutes = existing.SeedTimeMinutes ?? seed.Time,
            SeasonPackSeedTimeMinutes = existing.SeasonPackSeedTimeMinutes ?? seed.PackTime,
        });
        run.Updated(remote.Name);
        if (!hasKey)
        {
            run.Warn($"{updated.Name}: still no API key. Enter it in Indexers and switch the indexer on.", remote.Name);
        }

        return updated;
    }

    private static IndexerProtocol ProtocolOf(ArrIndexer indexer) => indexer.IsTorrent ? IndexerProtocol.Torrent : IndexerProtocol.Usenet;

    /// <summary>Radarr/Sonarr's Torznab fields: minimumSeeders, seedCriteria.seedRatio, seedCriteria.seedTime (minutes), Sonarr's seedCriteria.seasonPackSeedTime.</summary>
    private static (int MinimumSeeders, double? Ratio, int? Time, int? PackTime) SeedSettings(ArrIndexer indexer)
    {
        if (!indexer.IsTorrent)
        {
            return (1, null, null, null);
        }

        var minimum = indexer.Number("minimumSeeders") is >= 0 and <= IndexerService.MaxMinimumSeeders and var m ? (int)m : 1;
        var ratio = indexer.Number("seedCriteria.seedRatio") is > 0 and <= IndexerService.MaxSeedRatio and var r ? r : (double?)null;
        int? time = indexer.Number("seedCriteria.seedTime") is >= 1 and <= IndexerService.MaxSeedTimeMinutes and var t ? (int)t : null;
        int? pack = indexer.Number("seedCriteria.seasonPackSeedTime") is >= 1 and <= IndexerService.MaxSeedTimeMinutes and var p ? (int)p : null;
        return (minimum, ratio, time, pack);
    }

    private static string? SeedSummary(ArrIndexer indexer)
    {
        var (minimum, ratio, time, pack) = SeedSettings(indexer);
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"minimum {minimum} seeders") };
        if (ratio is { } r)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"seed ratio {r:0.##}"));
        }

        if (time is { } t)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"seed time {t} min"));
        }

        if (pack is { } p)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"season pack seed time {p} min"));
        }

        return string.Join(", ", parts);
    }

    private static string UniqueName(IndexerService service, string name)
    {
        var names = service.All().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name.Trim();
        if (!names.Contains(candidate))
        {
            return candidate;
        }

        for (var i = 2; ; i++)
        {
            var next = string.Create(CultureInfo.InvariantCulture, $"{candidate} ({i})");
            if (!names.Contains(next))
            {
                return next;
            }
        }
    }

    [GeneratedRegex(@"^/\d+/?$")]
    private static partial Regex ProwlarrPathRegex();
}
