// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Security;
using StreamZH.Data;

namespace StreamZH.Library.Indexers;

/// <summary>How an indexer is doing right now. <see cref="Healthy"/> is false while it is in back-off.</summary>
public sealed record IndexerHealth(
    int IndexerId,
    bool Healthy,
    DateTimeOffset? DisabledTill,
    DateTimeOffset? MostRecentFailure,
    string? LastError,
    int EscalationLevel)
{
    public static IndexerHealth Ok(int indexerId) => new(indexerId, true, null, null, null, 0);
}

/// <summary>Outcome of the "Test" button: the capabilities, or a message the user can act on.</summary>
public sealed record IndexerTestResult(IndexerCapabilities? Capabilities, string? Error)
{
    public bool Success => Capabilities is not null;
}

/// <summary>One row of "Test all".</summary>
public sealed record IndexerTestAllResult(int Id, string Name, bool Ok, string? Message);

/// <summary>Which limit stops the indexer right now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexerLimitKind>))]
public enum IndexerLimitKind
{
    [JsonStringEnumMemberName("query")]
    Query,

    [JsonStringEnumMemberName("grab")]
    Grab,
}

/// <summary>Use of the query and grab limits in the current window, and days until the VIP membership ends (negative: expired).</summary>
public sealed record IndexerLimitStatus(int QueriesUsed, int GrabsUsed, IndexerLimitKind? LimitReached, int? VipExpiresInDays)
{
    public static readonly IndexerLimitStatus None = new(0, 0, null, null);
}

/// <summary>Everything the details page shows; nothing here calls the indexer.</summary>
public sealed record IndexerDetails(IndexerDefinition Indexer, IndexerCatalogEntry? Definition, IndexerCapabilities? Capabilities, DateTimeOffset? CapabilitiesCheckedAt);

/// <summary>Indexers were added, changed, deleted, imported or tested, or their health changed (hub message "indexersChanged").</summary>
public sealed record IndexersChanged : IAppEvent;

/// <summary>
/// Wait times after repeated failures, like Prowlarr's EscalationBackOff (without its 1-minute step).
/// Level 0 means healthy; the first failure goes to level 1.
/// </summary>
public static class IndexerBackOff
{
    public static readonly IReadOnlyList<TimeSpan> Periods =
    [
        TimeSpan.Zero,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
    ];

    public static int MaxLevel => Periods.Count - 1;

    public static TimeSpan Period(int level) => Periods[Math.Clamp(level, 0, MaxLevel)];
}

/// <summary>Stores indexers, validates them, tests them and keeps track of their health.</summary>
public sealed class IndexerService(
    IndexerRepository indexers,
    IndexerStatusRepository statuses,
    TimeProvider time,
    ILogger<IndexerService> logger,
    IndexerHistoryRepository? history = null,
    IEventBus? events = null) : IIndexerProvider
{
    public const int MaxNameLength = 100;
    public const int MinPriority = 1;
    public const int MaxPriority = 50;
    public const int DefaultPriority = 25;
    public const string DefaultApiPath = "/api";

    /// <summary>What the API shows instead of a stored API key. Sent back unchanged, it means "keep the stored key".</summary>
    public const string MaskedApiKey = "********";

    /// <summary>A successful test is used for the capabilities of the indexer saved with the same connection within this time.</summary>
    public static readonly TimeSpan RecentTestLifetime = TimeSpan.FromMinutes(30);

    private const int MaxErrorLength = 500;

    /// <summary>Radarr's default Newznab movie categories.</summary>
    public static readonly IReadOnlyList<int> DefaultMovieCategories = [2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060];

    /// <summary>Sonarr's default Newznab TV categories.</summary>
    public static readonly IReadOnlyList<int> DefaultTvCategories = [5030, 5040];

    /// <summary>Torznab seed settings: "Minimum seeders" range, seed ratio and seed time limits (time in minutes, at most a year).</summary>
    public const int MaxMinimumSeeders = 1000;
    public const double MaxSeedRatio = 1000;
    public const int MaxSeedTimeMinutes = 525_600;

    private static readonly JsonSerializerOptions CapabilitiesJson = new(JsonSerializerDefaults.Web);

    private readonly Lock _statusLock = new();

    // Successful tests by connection (URL, path, key), so saving right after "Test" stores the capabilities.
    private readonly ConcurrentDictionary<string, (IndexerCapabilities Caps, DateTimeOffset At)> _recentTests = new(StringComparer.Ordinal);

    /// <summary>A new Newznab indexer to start from.</summary>
    public static IndexerDefinition Schema() => Schema(null);

    /// <summary>A new indexer of the implementation ("Newznab" or "Torznab"; anything else gives Newznab) to start from.</summary>
    public static IndexerDefinition Schema(string? implementation) => new(
        0,
        string.Empty,
        string.Equals(implementation?.Trim(), IndexerDefinition.Torznab, StringComparison.OrdinalIgnoreCase) ? IndexerDefinition.Torznab : IndexerDefinition.Newznab,
        string.Empty,
        DefaultApiPath,
        string.Empty,
        DefaultMovieCategories,
        DefaultTvCategories,
        DefaultPriority,
        EnableRss: true,
        EnableAutomaticSearch: true,
        EnableInteractiveSearch: true);

    /// <summary>Same base URL and API path: where a stored API key may be sent again (0.12).</summary>
    public static bool SameDestination(IndexerDefinition stored, IndexerDefinition changed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(changed);
        return SecretReuse.Same(stored.BaseUrl.TrimEnd('/'), changed.BaseUrl.TrimEnd('/'))
            && SecretReuse.Same(stored.ApiPath.Trim('/'), changed.ApiPath.Trim('/'));
    }

    /// <summary>All indexers ordered by priority, then name.</summary>
    public IReadOnlyList<IndexerDefinition> All() => indexers.All();

    public IndexerDefinition? Find(int id) => indexers.Find(id);

    /// <summary>
    /// Adds an indexer. With <paramref name="cloneFromId"/> ("Clone" in the UI) a masked API key means the stored key of that indexer.
    /// </summary>
    public IndexerDefinition Create(IndexerDefinition indexer, int? cloneFromId = null)
    {
        var normalized = Normalize(indexer) with { Id = 0 };
        var errors = Validate(normalized, null);
        if (normalized.ApiKey == MaskedApiKey && cloneFromId is > 0 && indexers.Find(cloneFromId.Value) is { } source)
        {
            // 0.12: a stored key only goes to the address it was entered for.
            if (SameDestination(source, normalized))
            {
                normalized = normalized with { ApiKey = source.ApiKey };
            }
            else
            {
                errors["apiKey"] = [SecretReuse.ChangedAddressMessage("API key", test: false)];
            }
        }

        if (normalized.ApiKey == MaskedApiKey)
        {
            errors.TryAdd("apiKey", ["Enter the API key."]);
        }

        ThrowIfAny(errors);
        var id = indexers.Add(normalized, time.GetUtcNow().UtcDateTime);
        StoreRecentTest(id, normalized);
        logger.LogInformation("Indexer '{Name}' added", normalized.Name);
        NotifyChanged();
        return indexers.Find(id)!;
    }

    public IndexerDefinition Update(int id, IndexerDefinition indexer)
    {
        var existing = indexers.Find(id) ?? throw new LibraryException("Indexer not found.", LibraryError.NotFound);
        var normalized = Normalize(indexer) with { Id = id };
        var errors = Validate(normalized, id);
        if (normalized.ApiKey == MaskedApiKey)
        {
            // 0.12: a stored key only goes to the address it was entered for.
            if (SameDestination(existing, normalized) || string.IsNullOrEmpty(existing.ApiKey))
            {
                normalized = normalized with { ApiKey = existing.ApiKey };
            }
            else
            {
                errors["apiKey"] = [SecretReuse.ChangedAddressMessage("API key", test: false)];
            }
        }

        ThrowIfAny(errors);
        indexers.Update(normalized);
        if (!StoreRecentTest(id, normalized) && ConnectionKey(existing) != ConnectionKey(normalized))
        {
            // Capabilities of another address or account would be misleading.
            indexers.SaveCapabilities(id, null, null);
        }

        logger.LogInformation("Indexer '{Name}' saved", normalized.Name);
        NotifyChanged();
        return indexers.Find(id)!;
    }

    public void Delete(int id)
    {
        var existing = indexers.Find(id) ?? throw new LibraryException("Indexer not found.", LibraryError.NotFound);
        indexers.Delete(id);
        logger.LogInformation("Indexer '{Name}' deleted", existing.Name);
        NotifyChanged();
    }

    /// <summary>
    /// Reads the capabilities without saving the indexer. A masked API key uses the stored key of the indexer with the same id.
    /// When the tested connection is the one of a saved indexer, its capabilities are stored.
    /// Throws <see cref="ValidationFailedException"/> when the address is not usable; connection problems come back as <see cref="IndexerTestResult.Error"/>.
    /// </summary>
    public async Task<IndexerTestResult> TestAsync(IndexerDefinition indexer, IIndexerClient client, CancellationToken cancellationToken, int? cloneFromId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var normalized = Normalize(indexer);
        var errors = ValidateConnection(normalized);
        IndexerDefinition? stored = normalized.Id > 0 ? indexers.Find(normalized.Id) : null;
        if (normalized.ApiKey == MaskedApiKey)
        {
            var keySource = stored ?? (cloneFromId is > 0 ? indexers.Find(cloneFromId.Value) : null);
            if (keySource is null)
            {
                errors["apiKey"] = ["Enter the API key."];
            }
            else if (SameDestination(keySource, normalized) || string.IsNullOrEmpty(keySource.ApiKey))
            {
                normalized = normalized with { ApiKey = keySource.ApiKey };
            }
            else
            {
                // 0.12: a stored key only goes to the address it was entered for.
                errors["apiKey"] = [SecretReuse.ChangedAddressMessage("API key", test: true)];
            }
        }

        ThrowIfAny(errors);

        // Only the saved indexer's history gets the call; an indexer being edited is tested with its stored id.
        if (stored is null)
        {
            normalized = normalized with { Id = 0 };
        }

        var host = new Uri(normalized.BaseUrl).Authority;
        using var source = IndexerCallContext.Begin(IndexerCallSource.Test);
        try
        {
            var caps = await client.GetCapabilitiesAsync(normalized, cancellationToken);
            if (UnsupportedCategories(normalized, caps) is { } categoryError)
            {
                logger.LogInformation("Indexer test for {Host}: {Message}", host, categoryError);
                return new IndexerTestResult(null, categoryError);
            }

            logger.LogInformation("Indexer test for {Host} succeeded ({Count} categories)", host, caps.Categories.Count);
            RememberTest(normalized, caps);
            if (stored is not null && ConnectionKey(stored) == ConnectionKey(normalized))
            {
                SaveCapabilities(stored.Id, caps);

                // The saved indexer works again (e.g. after its key was entered): no longer skipped.
                RecordSuccess(stored.Id, notify: true);
            }

            return new IndexerTestResult(caps, null);
        }
        catch (IndexerException ex)
        {
            logger.LogInformation("Indexer test for {Host} failed: {Message}", host, ex.Message);
            var message = ex.IsAuthError && !ex.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                ? $"{ex.Message} Check the API key."
                : ex.Message;
            return new IndexerTestResult(null, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Indexer test for {Host} timed out", host);
            return new IndexerTestResult(null, $"{host} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Indexer test for {Host} failed: {Message}", host, ex.Message);
            return new IndexerTestResult(null, $"Could not connect to {host}: {ex.Message}");
        }
    }

    /// <summary>Tests every enabled indexer in parallel with its stored settings and records the outcome in its health.</summary>
    public async Task<IReadOnlyList<IndexerTestAllResult>> TestAllAsync(IIndexerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var enabled = indexers.All().Where(i => i.Enable).ToList();
        var results = await Task.WhenAll(enabled.Select(async indexer =>
        {
            string? message;
            try
            {
                message = (await TestAsync(indexer, client, cancellationToken)).Error;
            }
            catch (ValidationFailedException ex)
            {
                message = string.Join(" ", ex.Errors.Values.SelectMany(v => v));
            }

            if (message is null)
            {
                RecordSuccess(indexer.Id, notify: false);
            }
            else
            {
                RecordFailure(indexer.Id, new IndexerException(message), notify: false);
            }

            return new IndexerTestAllResult(indexer.Id, indexer.Name, message is null, message);
        }));

        logger.LogInformation("Tested {Count} indexers, {Failed} failed", results.Length, results.Count(r => !r.Ok));
        NotifyChanged();
        return results;
    }

    /// <summary>The indexer with its catalog entry and the capabilities stored by the last successful test.</summary>
    public IndexerDetails? Details(int id)
    {
        if (indexers.Find(id) is not { } indexer)
        {
            return null;
        }

        var (json, checkedAt) = indexers.FindCapabilities(id);
        IndexerCapabilities? caps = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                caps = JsonSerializer.Deserialize<IndexerCapabilities>(json, CapabilitiesJson);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Stored capabilities of indexer {Id} are unreadable: {Message}", id, ex.Message);
            }
        }

        return new IndexerDetails(
            indexer,
            IndexerCatalog.For(indexer),
            caps,
            caps is not null && checkedAt is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null);
    }

    // ---- Health ----

    /// <summary>Health of every indexer that has a status; indexers without one are healthy.</summary>
    public IReadOnlyDictionary<int, IndexerHealth> Health()
    {
        var now = time.GetUtcNow();
        return statuses.All().ToDictionary(s => s.IndexerId, s => ToHealth(s, now));
    }

    public IndexerHealth Health(int indexerId) =>
        statuses.Find(indexerId) is { } status ? ToHealth(status, time.GetUtcNow()) : IndexerHealth.Ok(indexerId);

    /// <summary>Calls in the current limit windows of every indexer that made calls in the last 24 hours.</summary>
    public IReadOnlyDictionary<int, IndexerUsageCounts> Usage() =>
        history?.Usage(time.GetUtcNow().UtcDateTime) ?? new Dictionary<int, IndexerUsageCounts>();

    /// <summary>Limit use and VIP days of one indexer, from its usage counts.</summary>
    public IndexerLimitStatus LimitStatus(IndexerDefinition indexer, IndexerUsageCounts? usage)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        var counts = usage ?? IndexerUsageCounts.None;
        var queries = counts.Queries(indexer.LimitsUnit);
        var grabs = counts.Grabs(indexer.LimitsUnit);
        IndexerLimitKind? reached = indexer.QueryLimit is > 0 and var q && queries >= q ? IndexerLimitKind.Query
            : indexer.GrabLimit is > 0 and var g && grabs >= g ? IndexerLimitKind.Grab
            : null;

        int? vipDays = indexer.VipExpirationDate is { } vip
            ? vip.DayNumber - DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).DayNumber
            : null;
        return new IndexerLimitStatus(queries, grabs, reached, vipDays);
    }

    public IReadOnlyList<IndexerDefinition> Active(IndexerUse use)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var blocked = statuses.All().Where(s => s.DisabledTill > now).Select(s => s.IndexerId).ToHashSet();
        return [.. indexers.All()
            .Where(i => i.IsEnabledFor(use) && !blocked.Contains(i.Id))
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Back to healthy: the escalation starts from zero after the next failure.</summary>
    public void RecordSuccess(int indexerId) => RecordSuccess(indexerId, notify: true);

    /// <summary>
    /// Escalates the back-off one level and skips the indexer until the wait is over.
    /// A longer <see cref="IndexerException.RetryAfter"/> from the indexer wins. StreamZH's own limits
    /// (<see cref="IndexerException.IsLimitReached"/>) are not failures and are ignored.
    /// </summary>
    public void RecordFailure(int indexerId, Exception error) => RecordFailure(indexerId, error, notify: true);

    private void RecordSuccess(int indexerId, bool notify)
    {
        if (indexerId <= 0)
        {
            return;
        }

        lock (_statusLock)
        {
            if (statuses.Find(indexerId) is null)
            {
                return;
            }

            statuses.Delete(indexerId);
        }

        logger.LogInformation("Indexer {Id} is working again", indexerId);
        if (notify)
        {
            NotifyChanged();
        }
    }

    private void RecordFailure(int indexerId, Exception error, bool notify)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is IndexerException { IsLimitReached: true } || indexerId <= 0 || indexers.Find(indexerId) is not { } indexer)
        {
            return;
        }

        IndexerStatus status;
        lock (_statusLock)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var previous = statuses.Find(indexerId);
            var level = previous is null || previous.EscalationLevel == 0 ? 1 : Math.Min(IndexerBackOff.MaxLevel, previous.EscalationLevel + 1);
            var initial = previous is null || previous.EscalationLevel == 0 ? now : previous.InitialFailure ?? now;

            var wait = IndexerBackOff.Period(level);
            if (error is IndexerException { RetryAfter: { } retryAfter } && retryAfter > wait)
            {
                wait = retryAfter;
            }

            var message = error.Message.Length > MaxErrorLength ? error.Message[..MaxErrorLength] : error.Message;
            status = new IndexerStatus(indexerId, initial, now, level, now + wait, message);
            statuses.Upsert(status);
        }

        logger.LogWarning("Indexer '{Name}' failed ({Message}); skipped until {DisabledTill:u}", indexer.Name, status.LastError, status.DisabledTill);
        if (notify)
        {
            NotifyChanged();
        }
    }

    /// <summary>Tells the browsers that the indexer list changed. Never throws.</summary>
    public void NotifyChanged()
    {
        if (events is not null)
        {
            _ = PublishChangedAsync(events);
        }
    }

    private async Task PublishChangedAsync(IEventBus bus)
    {
        try
        {
            await bus.PublishAsync(new IndexersChanged());
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not publish the indexer change: {Message}", ex.Message);
        }
    }

    // ---- Capabilities ----

    private void SaveCapabilities(int id, IndexerCapabilities caps) =>
        indexers.SaveCapabilities(id, JsonSerializer.Serialize(caps, CapabilitiesJson), time.GetUtcNow().UtcDateTime);

    private void RememberTest(IndexerDefinition indexer, IndexerCapabilities caps)
    {
        var now = time.GetUtcNow();
        foreach (var old in _recentTests.Where(e => now - e.Value.At > RecentTestLifetime).Select(e => e.Key).ToList())
        {
            _recentTests.TryRemove(old, out _);
        }

        _recentTests[ConnectionKey(indexer)] = (caps, now);
    }

    /// <summary>Stores the capabilities of a recent successful test of the same connection; false when there was none.</summary>
    private bool StoreRecentTest(int id, IndexerDefinition indexer)
    {
        if (!_recentTests.TryGetValue(ConnectionKey(indexer), out var test) || time.GetUtcNow() - test.At > RecentTestLifetime)
        {
            return false;
        }

        SaveCapabilities(id, test.Caps);
        return true;
    }

    private static string ConnectionKey(IndexerDefinition indexer) =>
        $"{indexer.BaseUrl.TrimEnd('/')}\n{indexer.ApiPath}\n{indexer.ApiKey}\n{indexer.AdditionalParameters}";

    /// <summary>
    /// Radarr's Torznab test: when the indexer lists its categories and none of the selected ones is among them, searches
    /// would find nothing (Jackett and Prowlarr indexers offer only some of the 2000/5000 trees).
    /// </summary>
    private static string? UnsupportedCategories(IndexerDefinition indexer, IndexerCapabilities caps)
    {
        if (indexer.Protocol != Core.Downloads.DownloadProtocol.Torrent || caps.Categories.Count == 0)
        {
            return null;
        }

        var offered = caps.Categories.SelectMany(c => c.SubCategories.Select(s => s.Id).Prepend(c.Id)).ToHashSet();
        List<int> selected = [.. indexer.Categories.Concat(indexer.TvCategories).Distinct()];
        return selected.Count > 0 && !selected.Any(offered.Contains)
            ? $"This indexer supports none of the selected categories ({string.Join(", ", selected.Order())}). Choose categories it offers."
            : null;
    }

    // ---- Validation ----

    private Dictionary<string, string[]> Validate(IndexerDefinition indexer, int? existingId)
    {
        var errors = ValidateConnection(indexer);

        if (string.IsNullOrWhiteSpace(indexer.Name))
        {
            errors["name"] = ["Enter a name."];
        }
        else if (indexer.Name.Length > MaxNameLength)
        {
            errors["name"] = [$"Use at most {MaxNameLength} characters."];
        }
        else if (indexers.FindByName(indexer.Name) is { } other && other.Id != existingId)
        {
            errors["name"] = ["An indexer with this name already exists."];
        }

        if (indexer.Priority is < MinPriority or > MaxPriority)
        {
            errors["priority"] = [$"Use a priority from {MinPriority} (highest) to {MaxPriority}."];
        }

        if (indexer.Categories.Count == 0 && indexer.TvCategories.Count == 0)
        {
            errors["categories"] = ["Choose at least one movie or TV category."];
        }
        else if (indexer.Categories.Concat(indexer.TvCategories).Any(c => c <= 0))
        {
            errors["categories"] = ["Category ids must be positive numbers."];
        }

        if (indexer.DefinitionKey is { } key)
        {
            var entry = IndexerCatalog.Find(key);
            if (entry is null)
            {
                errors["definitionKey"] = ["Unknown indexer definition."];
            }
            else if (!entry.Supported)
            {
                errors["definitionKey"] = [entry.UnsupportedReason ?? "This indexer is not supported yet."];
            }
            else if (!string.Equals(entry.Implementation, indexer.Implementation, StringComparison.OrdinalIgnoreCase))
            {
                errors["definitionKey"] = [$"{entry.Name} is a {entry.Implementation} indexer."];
            }
        }

        if (indexer.Protocol == Core.Downloads.DownloadProtocol.Torrent)
        {
            ValidateSeedSettings(indexer, errors);
        }

        if (indexer.VipExpiration is not null && indexer.VipExpirationDate is null)
        {
            errors["vipExpiration"] = ["Enter a date like 2027-01-31."];
        }

        if (indexer.QueryLimit < 0)
        {
            errors["queryLimit"] = ["Use 0 or more (0 or empty = unlimited)."];
        }

        if (indexer.GrabLimit < 0)
        {
            errors["grabLimit"] = ["Use 0 or more (0 or empty = unlimited)."];
        }

        return errors;
    }

    /// <summary>The fields needed to reach the indexer: implementation, URL, API path and additional parameters.</summary>
    private static Dictionary<string, string[]> ValidateConnection(IndexerDefinition indexer)
    {
        var errors = new Dictionary<string, string[]>();

        if (!string.Equals(indexer.Implementation, IndexerDefinition.Newznab, StringComparison.Ordinal)
            && !string.Equals(indexer.Implementation, IndexerDefinition.Torznab, StringComparison.Ordinal))
        {
            errors["implementation"] = ["Use Newznab (Usenet) or Torznab (torrents)."];
        }

        if (string.IsNullOrWhiteSpace(indexer.BaseUrl))
        {
            errors["baseUrl"] = ["Enter the indexer's URL."];
        }
        else if (!Uri.TryCreate(indexer.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors["baseUrl"] = ["Enter a full http:// or https:// address, e.g. https://indexer.example."];
        }

        if (SecretReuse.StoredFormProblem(indexer.ApiKey) is { } storedForm)
        {
            errors["apiKey"] = [storedForm];
        }

        if (indexer.ApiPath.Any(char.IsWhiteSpace) || indexer.ApiPath.Contains('?', StringComparison.Ordinal) || indexer.ApiPath.Contains('#', StringComparison.Ordinal))
        {
            errors["apiPath"] = ["Enter a path like /api, without spaces or query."];
        }

        if (indexer.AdditionalParameters.Any(char.IsWhiteSpace) || indexer.AdditionalParameters.Contains('#', StringComparison.Ordinal))
        {
            errors["additionalParameters"] = ["Enter parameters like &attrs=poster, without spaces or #."];
        }

        // Radarr: Jackett's "all" endpoint mixes every tracker into one feed and breaks paging and categories.
        if (indexer.Protocol == Core.Downloads.DownloadProtocol.Torrent && IsJackettAll(indexer))
        {
            errors["baseUrl"] = ["Jackett's 'all' endpoint is not supported. Add each Jackett indexer on its own (its Torznab feed URL)."];
        }

        return errors;
    }

    private static bool IsJackettAll(IndexerDefinition indexer) =>
        new[] { indexer.BaseUrl, indexer.ApiPath }.Any(v =>
            v.Contains("/torznab/all", StringComparison.OrdinalIgnoreCase)
            || v.Contains("/api/v2.0/indexers/all/results/torznab", StringComparison.OrdinalIgnoreCase));

    /// <summary>Minimum seeders 0 to 1000; seed ratio and times empty (= the download client's setting) or positive.</summary>
    private static void ValidateSeedSettings(IndexerDefinition indexer, Dictionary<string, string[]> errors)
    {
        if (indexer.MinimumSeeders is < 0 or > MaxMinimumSeeders)
        {
            errors["minimumSeeders"] = [$"Use 0 to {MaxMinimumSeeders} (1 is the usual minimum)."];
        }

        if (indexer.SeedRatio is { } ratio && (!double.IsFinite(ratio) || ratio <= 0 || ratio > MaxSeedRatio))
        {
            errors["seedRatio"] = [$"Use a ratio above 0 and up to {MaxSeedRatio:0}, or leave it empty to use the download client's setting."];
        }

        if (indexer.SeedTimeMinutes is < 1 or > MaxSeedTimeMinutes)
        {
            errors["seedTimeMinutes"] = [$"Use 1 to {MaxSeedTimeMinutes} minutes, or leave it empty to use the download client's setting."];
        }

        if (indexer.SeasonPackSeedTimeMinutes is < 1 or > MaxSeedTimeMinutes)
        {
            errors["seasonPackSeedTimeMinutes"] = [$"Use 1 to {MaxSeedTimeMinutes} minutes, or leave it empty to use the seed time."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    /// <summary>Trims text, removes the trailing slash of the URL and fills in the defaults.</summary>
    private static IndexerDefinition Normalize(IndexerDefinition indexer)
    {
        var apiPath = indexer.ApiPath?.Trim() ?? string.Empty;
        if (apiPath.Length == 0)
        {
            apiPath = DefaultApiPath;
        }
        else if (!apiPath.StartsWith('/'))
        {
            apiPath = "/" + apiPath;
        }

        var implementation = indexer.Implementation?.Trim() switch
        {
            null or "" => IndexerDefinition.Newznab,
            var i when i.Equals(IndexerDefinition.Newznab, StringComparison.OrdinalIgnoreCase) => IndexerDefinition.Newznab,
            var i when i.Equals(IndexerDefinition.Torznab, StringComparison.OrdinalIgnoreCase) => IndexerDefinition.Torznab,
            var i => i,
        };
        var torrent = implementation == IndexerDefinition.Torznab;

        return indexer with
        {
            Name = indexer.Name?.Trim() ?? string.Empty,
            Implementation = implementation,

            // Seed settings only mean something for torrents; Usenet indexers keep the defaults.
            MinimumSeeders = torrent ? indexer.MinimumSeeders : 1,
            SeedRatio = torrent ? indexer.SeedRatio : null,
            SeedTimeMinutes = torrent ? indexer.SeedTimeMinutes : null,
            SeasonPackSeedTimeMinutes = torrent ? indexer.SeasonPackSeedTimeMinutes : null,
            BaseUrl = (indexer.BaseUrl?.Trim() ?? string.Empty).TrimEnd('/'),
            ApiPath = apiPath.Length > 1 ? apiPath.TrimEnd('/') : apiPath,
            ApiKey = indexer.ApiKey?.Trim() ?? string.Empty,
            Categories = [.. (indexer.Categories ?? []).Distinct().Order()],
            TvCategories = [.. (indexer.TvCategories ?? []).Distinct().Order()],
            DefinitionKey = string.IsNullOrWhiteSpace(indexer.DefinitionKey) ? null : indexer.DefinitionKey.Trim().ToLowerInvariant(),
            AdditionalParameters = indexer.AdditionalParameters?.Trim() ?? string.Empty,
            VipExpiration = NormalizeDate(indexer.VipExpiration),
        };
    }

    /// <summary>"2027-01-31", "2027-01-31T00:00:00Z" and similar become "2027-01-31"; empty becomes null; anything else stays (and fails validation).</summary>
    private static string? NormalizeDate(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime)
            ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : text;
    }

    private static IndexerHealth ToHealth(IndexerStatus status, DateTimeOffset now)
    {
        var disabledTill = status.DisabledTill is { } till ? new DateTimeOffset(till, TimeSpan.Zero) : (DateTimeOffset?)null;
        var mostRecent = status.MostRecentFailure is { } last ? new DateTimeOffset(last, TimeSpan.Zero) : (DateTimeOffset?)null;
        var blocked = disabledTill > now;
        return new IndexerHealth(status.IndexerId, !blocked, blocked ? disabledTill : null, mostRecent, status.LastError, status.EscalationLevel);
    }
}
