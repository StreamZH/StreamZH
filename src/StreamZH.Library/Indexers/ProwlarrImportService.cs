// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Indexers;

namespace StreamZH.Library.Indexers;

/// <summary>How an imported indexer is reached.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProwlarrImportMode>))]
public enum ProwlarrImportMode
{
    /// <summary>The indexer's own URL and API key could be read from Prowlarr.</summary>
    [JsonStringEnumMemberName("direct")]
    Direct,

    /// <summary>Through Prowlarr's Newznab proxy (http://prowlarr:9696/{id}/api with Prowlarr's API key).</summary>
    [JsonStringEnumMemberName("proxy")]
    Proxy,
}

/// <summary>One Prowlarr indexer as it would be imported. API keys are never part of it.</summary>
public sealed record ProwlarrIndexerPreview(
    int ProwlarrId,
    string Name,
    string Protocol,
    string Implementation,
    string BaseUrl,
    string ApiPath,
    bool Supported,
    string? Reason,
    bool AlreadyExists,
    ProwlarrImportMode Mode);

public sealed record ProwlarrImported(int Id, string Name, ProwlarrImportMode Mode);

public sealed record ProwlarrSkipped(int ProwlarrId, string Name, string Reason);

public sealed record ProwlarrImportResult(IReadOnlyList<ProwlarrImported> Imported, IReadOnlyList<ProwlarrSkipped> Skipped);

/// <summary>
/// Reads the indexers of a Prowlarr instance (GET /api/v1/indexer with X-Api-Key) and creates matching Newznab indexers.
/// Prowlarr masks API keys in its API ("********"), so usually the indexers are reached through Prowlarr's Newznab
/// proxy; when the key is readable (or the indexer has none), the indexer is used directly.
/// </summary>
public sealed class ProwlarrImportService(HttpClient http, IndexerService indexers, ILogger<ProwlarrImportService> logger)
{
    public const string TorrentReason = "Torrent indexers follow in StreamZH 1.0";

    /// <summary>Prowlarr's placeholder for API keys and passwords (SchemaBuilder.PRIVATE_VALUE).</summary>
    public const string MaskedValue = "********";

    public async Task<IReadOnlyList<ProwlarrIndexerPreview>> PreviewAsync(string? url, string? apiKey, CancellationToken cancellationToken)
    {
        var (baseUrl, key) = ValidateConnection(url, apiKey);
        var remote = await FetchAsync(baseUrl, key, cancellationToken);
        var existing = ExistingNames();
        return [.. remote.Select(r => Plan(r, baseUrl, key, existing).Preview)];
    }

    /// <summary>Imports the selected indexers; the rest of the list (and anything that fails validation) is reported as skipped.</summary>
    public async Task<ProwlarrImportResult> ImportAsync(string? url, string? apiKey, IReadOnlyCollection<int>? ids, CancellationToken cancellationToken)
    {
        var (baseUrl, key) = ValidateConnection(url, apiKey);
        var wanted = (ids ?? []).ToHashSet();
        var remote = await FetchAsync(baseUrl, key, cancellationToken);
        var existing = ExistingNames();
        var imported = new List<ProwlarrImported>();
        var skipped = new List<ProwlarrSkipped>();

        foreach (var id in wanted.Where(id => remote.All(r => r.Id != id)).Order())
        {
            skipped.Add(new ProwlarrSkipped(id, string.Empty, "Not found in Prowlarr."));
        }

        foreach (var indexer in remote.Where(r => wanted.Contains(r.Id)))
        {
            var (preview, definition) = Plan(indexer, baseUrl, key, existing);
            if (!preview.Supported || definition is null)
            {
                skipped.Add(new ProwlarrSkipped(indexer.Id, indexer.Name, preview.Reason ?? "Not supported."));
                continue;
            }

            if (preview.AlreadyExists)
            {
                skipped.Add(new ProwlarrSkipped(indexer.Id, indexer.Name, "An indexer with this name already exists."));
                continue;
            }

            try
            {
                var created = indexers.Create(definition);
                existing.Add(created.Name);
                imported.Add(new ProwlarrImported(created.Id, created.Name, preview.Mode));
            }
            catch (ValidationFailedException ex)
            {
                skipped.Add(new ProwlarrSkipped(indexer.Id, indexer.Name, string.Join(" ", ex.Errors.Values.SelectMany(v => v))));
            }
        }

        logger.LogInformation("Imported {Imported} indexers from Prowlarr at {Host}, skipped {Skipped}", imported.Count, new Uri(baseUrl).Authority, skipped.Count);
        indexers.NotifyChanged();
        return new ProwlarrImportResult(imported, skipped);
    }

    private HashSet<string> ExistingNames() => indexers.All().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (string BaseUrl, string ApiKey) ValidateConnection(string? url, string? apiKey)
    {
        var errors = new Dictionary<string, string[]>();
        var baseUrl = url?.Trim().TrimEnd('/') ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            errors["url"] = ["Enter Prowlarr's address, e.g. http://localhost:9696."];
        }
        else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors["url"] = ["Enter a full http:// or https:// address, e.g. http://localhost:9696."];
        }

        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            errors["apiKey"] = ["Enter Prowlarr's API key (Settings > General)."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return (baseUrl, key);
    }

    private async Task<List<RemoteIndexer>> FetchAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var host = new Uri(baseUrl).Authority;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/indexer");
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Invalid("url", $"Prowlarr at {host} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Prowlarr at {Host} is not reachable: {Message}", host, ex.Message);
            throw Invalid("url", $"Could not connect to Prowlarr at {host}: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw Invalid("apiKey", "Prowlarr rejected the API key.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw Invalid("url", $"No Prowlarr API found at {host}. Check the address (and the URL base, if Prowlarr uses one).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Invalid("url", string.Create(CultureInfo.InvariantCulture, $"Prowlarr at {host} answered with HTTP {(int)response.StatusCode}."));
            }

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid("url", $"{host} did not return a Prowlarr indexer list.");
                }

                return [.. document.RootElement.EnumerateArray().Select(RemoteIndexer.Parse).OfType<RemoteIndexer>()];
            }
            catch (JsonException)
            {
                throw Invalid("url", $"{host} did not return a Prowlarr indexer list.");
            }
        }
    }

    /// <summary>Decides support and mode for one Prowlarr indexer and builds the definition to create.</summary>
    private static (ProwlarrIndexerPreview Preview, IndexerDefinition? Definition) Plan(RemoteIndexer remote, string prowlarrUrl, string prowlarrKey, HashSet<string> existing)
    {
        var ownUrl = remote.Text("baseUrl")?.Trim().TrimEnd('/');
        var ownKey = remote.Text("apiKey");
        var hasKeyField = remote.Fields.ContainsKey("apiKey");
        var newznabLike = string.Equals(remote.Implementation, IndexerDefinition.Newznab, StringComparison.OrdinalIgnoreCase) || remote.Fields.ContainsKey("apiPath");
        var direct = remote.Protocol == "usenet" && newznabLike && hasKeyField && !string.IsNullOrEmpty(ownUrl)
            && Uri.TryCreate(ownUrl, UriKind.Absolute, out _) && ownKey != MaskedValue;
        var mode = direct ? ProwlarrImportMode.Direct : ProwlarrImportMode.Proxy;

        var baseUrl = direct ? ownUrl! : string.Create(CultureInfo.InvariantCulture, $"{prowlarrUrl}/{remote.Id}");
        var apiPath = direct ? NonEmpty(remote.Text("apiPath")) ?? IndexerService.DefaultApiPath : IndexerService.DefaultApiPath;

        var (supported, reason) = remote.Protocol switch
        {
            "usenet" => (true, (string?)null),
            "torrent" => (false, TorrentReason),
            _ => (false, "Unknown protocol."),
        };

        var preview = new ProwlarrIndexerPreview(
            remote.Id,
            remote.Name,
            remote.Protocol,
            remote.Implementation,
            baseUrl,
            apiPath,
            supported,
            reason,
            existing.Contains(remote.Name.Trim()),
            mode);

        if (!supported)
        {
            return (preview, null);
        }

        List<int> movies = [.. remote.Categories.Where(c => c is >= 2000 and < 3000).Distinct().Order()];
        List<int> tv = [.. remote.Categories.Where(c => c is >= 5000 and < 6000).Distinct().Order()];
        var unit = remote.Number("baseSettings.limitsUnit") == 1 ? IndexerLimitsUnit.Hour : IndexerLimitsUnit.Day;

        var definition = new IndexerDefinition(
            0,
            remote.Name.Trim(),
            IndexerDefinition.Newznab,
            baseUrl,
            apiPath,
            direct ? ownKey ?? string.Empty : prowlarrKey,
            movies.Count > 0 ? movies : IndexerService.DefaultMovieCategories,
            tv.Count > 0 ? tv : IndexerService.DefaultTvCategories,
            Math.Clamp(remote.Priority ?? IndexerService.DefaultPriority, IndexerService.MinPriority, IndexerService.MaxPriority),
            EnableRss: remote.SupportsRss,
            EnableAutomaticSearch: remote.SupportsSearch,
            EnableInteractiveSearch: remote.SupportsSearch)
        {
            Enable = remote.Enable,
            DefinitionKey = IndexerCatalog.Match(remote.Name, direct ? ownUrl : null).Key,

            // Through the proxy Prowlarr adds the indexer's extra parameters itself.
            AdditionalParameters = direct ? remote.Text("additionalParameters")?.Trim() ?? string.Empty : string.Empty,
            VipExpiration = NonEmpty(remote.Text("vipExpiration")),
            QueryLimit = remote.Number("baseSettings.queryLimit") is > 0 and var q ? q : null,
            GrabLimit = remote.Number("baseSettings.grabLimit") is > 0 and var g ? g : null,
            LimitsUnit = unit,
        };

        return (preview, definition);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ValidationFailedException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The parts of Prowlarr's IndexerResource that matter here.</summary>
    private sealed record RemoteIndexer(
        int Id,
        string Name,
        string Implementation,
        string Protocol,
        bool Enable,
        bool SupportsRss,
        bool SupportsSearch,
        int? Priority,
        IReadOnlyDictionary<string, JsonElement> Fields,
        IReadOnlyList<int> Categories)
    {
        public static RemoteIndexer? Parse(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                return null;
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

            var categories = new List<int>();
            if (item.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("categories", out var tree) && tree.ValueKind == JsonValueKind.Array)
            {
                Collect(tree, categories);
            }

            return new RemoteIndexer(
                id,
                String(item, "name") ?? string.Create(CultureInfo.InvariantCulture, $"Prowlarr {id}"),
                String(item, "implementation") ?? string.Empty,
                ProtocolOf(item),
                Bool(item, "enable") ?? true,
                Bool(item, "supportsRss") ?? true,
                Bool(item, "supportsSearch") ?? true,
                item.TryGetProperty("priority", out var priority) && priority.TryGetInt32(out var p) ? p : null,
                fields,
                categories);
        }

        public string? Text(string field) =>
            Fields.TryGetValue(field, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            } : null;

        public int? Number(string field) =>
            Fields.TryGetValue(field, out var value) ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            } : null;

        private static void Collect(JsonElement tree, List<int> into)
        {
            foreach (var category in tree.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (category.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                {
                    into.Add(value);
                }

                if (category.TryGetProperty("subCategories", out var subs) && subs.ValueKind == JsonValueKind.Array)
                {
                    Collect(subs, into);
                }
            }
        }

        // Prowlarr writes enums as camelCase strings; numbers (1 usenet, 2 torrent) are accepted too.
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

        private static string? String(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool? Bool(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
}
