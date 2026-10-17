// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only
// Portions based on Prowlarr (https://github.com/Prowlarr/Prowlarr), Copyright (c) Prowlarr contributors, GPL-3.0.

using Microsoft.Extensions.Logging;
using StreamZH.Core.Indexers;
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
    ILogger<IndexerService> logger) : IIndexerProvider
{
    public const int MaxNameLength = 100;
    public const int MinPriority = 1;
    public const int MaxPriority = 50;
    public const int DefaultPriority = 25;
    public const string DefaultApiPath = "/api";

    /// <summary>What the API shows instead of a stored API key. Sent back unchanged, it means "keep the stored key".</summary>
    public const string MaskedApiKey = "********";

    private const int MaxErrorLength = 500;

    /// <summary>Radarr's default Newznab movie categories.</summary>
    public static readonly IReadOnlyList<int> DefaultMovieCategories = [2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060];

    /// <summary>Sonarr's default Newznab TV categories.</summary>
    public static readonly IReadOnlyList<int> DefaultTvCategories = [5030, 5040];

    private readonly Lock _statusLock = new();

    /// <summary>A new Newznab indexer to start from.</summary>
    public static IndexerDefinition Schema() => new(
        0,
        string.Empty,
        IndexerDefinition.Newznab,
        string.Empty,
        DefaultApiPath,
        string.Empty,
        DefaultMovieCategories,
        DefaultTvCategories,
        DefaultPriority,
        EnableRss: true,
        EnableAutomaticSearch: true,
        EnableInteractiveSearch: true);

    /// <summary>All indexers ordered by priority, then name.</summary>
    public IReadOnlyList<IndexerDefinition> All() => indexers.All();

    public IndexerDefinition? Find(int id) => indexers.Find(id);

    public IndexerDefinition Create(IndexerDefinition indexer)
    {
        var normalized = Normalize(indexer) with { Id = 0 };
        var errors = Validate(normalized, null);
        if (normalized.ApiKey == MaskedApiKey)
        {
            errors["apiKey"] = ["Enter the API key."];
        }

        ThrowIfAny(errors);
        var id = indexers.Add(normalized, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Indexer '{Name}' added", normalized.Name);
        return indexers.Find(id)!;
    }

    public IndexerDefinition Update(int id, IndexerDefinition indexer)
    {
        var existing = indexers.Find(id) ?? throw new LibraryException("Indexer not found.", LibraryError.NotFound);
        var normalized = Normalize(indexer) with { Id = id };
        if (normalized.ApiKey == MaskedApiKey)
        {
            normalized = normalized with { ApiKey = existing.ApiKey };
        }

        ThrowIfAny(Validate(normalized, id));
        indexers.Update(normalized);
        logger.LogInformation("Indexer '{Name}' saved", normalized.Name);
        return indexers.Find(id)!;
    }

    public void Delete(int id)
    {
        var existing = indexers.Find(id) ?? throw new LibraryException("Indexer not found.", LibraryError.NotFound);
        indexers.Delete(id);
        logger.LogInformation("Indexer '{Name}' deleted", existing.Name);
    }

    /// <summary>
    /// Reads the capabilities without saving anything. A masked API key uses the stored key of the indexer with the same id.
    /// Throws <see cref="ValidationFailedException"/> when the address is not usable; connection problems come back as <see cref="IndexerTestResult.Error"/>.
    /// </summary>
    public async Task<IndexerTestResult> TestAsync(IndexerDefinition indexer, IIndexerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var normalized = Normalize(indexer);
        var errors = ValidateConnection(normalized);
        if (normalized.ApiKey == MaskedApiKey)
        {
            if (normalized.Id > 0 && indexers.Find(normalized.Id) is { } stored)
            {
                normalized = normalized with { ApiKey = stored.ApiKey };
            }
            else
            {
                errors["apiKey"] = ["Enter the API key."];
            }
        }

        ThrowIfAny(errors);

        var host = new Uri(normalized.BaseUrl).Authority;
        try
        {
            var caps = await client.GetCapabilitiesAsync(normalized, cancellationToken);
            logger.LogInformation("Indexer test for {Host} succeeded ({Count} categories)", host, caps.Categories.Count);
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

    // ---- Health ----

    /// <summary>Health of every indexer that has a status; indexers without one are healthy.</summary>
    public IReadOnlyDictionary<int, IndexerHealth> Health()
    {
        var now = time.GetUtcNow();
        return statuses.All().ToDictionary(s => s.IndexerId, s => ToHealth(s, now));
    }

    public IndexerHealth Health(int indexerId) =>
        statuses.Find(indexerId) is { } status ? ToHealth(status, time.GetUtcNow()) : IndexerHealth.Ok(indexerId);

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
    public void RecordSuccess(int indexerId)
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
    }

    /// <summary>
    /// Escalates the back-off one level and skips the indexer until the wait is over.
    /// A longer <see cref="IndexerException.RetryAfter"/> from the indexer wins.
    /// </summary>
    public void RecordFailure(int indexerId, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (indexerId <= 0 || indexers.Find(indexerId) is not { } indexer)
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

        return errors;
    }

    /// <summary>The fields needed to reach the indexer: implementation, URL and API path.</summary>
    private static Dictionary<string, string[]> ValidateConnection(IndexerDefinition indexer)
    {
        var errors = new Dictionary<string, string[]>();

        if (!string.Equals(indexer.Implementation, IndexerDefinition.Newznab, StringComparison.Ordinal))
        {
            errors["implementation"] = ["Only Newznab indexers are supported."];
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

        if (indexer.ApiPath.Any(char.IsWhiteSpace) || indexer.ApiPath.Contains('?', StringComparison.Ordinal) || indexer.ApiPath.Contains('#', StringComparison.Ordinal))
        {
            errors["apiPath"] = ["Enter a path like /api, without spaces or query."];
        }

        return errors;
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

        return indexer with
        {
            Name = indexer.Name?.Trim() ?? string.Empty,
            Implementation = string.IsNullOrWhiteSpace(indexer.Implementation) ? IndexerDefinition.Newznab : indexer.Implementation.Trim(),
            BaseUrl = (indexer.BaseUrl?.Trim() ?? string.Empty).TrimEnd('/'),
            ApiPath = apiPath.Length > 1 ? apiPath.TrimEnd('/') : apiPath,
            ApiKey = indexer.ApiKey?.Trim() ?? string.Empty,
            Categories = [.. (indexer.Categories ?? []).Distinct().Order()],
            TvCategories = [.. (indexer.TvCategories ?? []).Distinct().Order()],
        };
    }

    private static IndexerHealth ToHealth(IndexerStatus status, DateTimeOffset now)
    {
        var disabledTill = status.DisabledTill is { } till ? new DateTimeOffset(till, TimeSpan.Zero) : (DateTimeOffset?)null;
        var mostRecent = status.MostRecentFailure is { } last ? new DateTimeOffset(last, TimeSpan.Zero) : (DateTimeOffset?)null;
        var blocked = disabledTill > now;
        return new IndexerHealth(status.IndexerId, !blocked, blocked ? disabledTill : null, mostRecent, status.LastError, status.EscalationLevel);
    }
}
