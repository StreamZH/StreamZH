// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Indexers: release rules for all indexers and automation (<c>/settings/indexers</c>) and the release
/// profiles (<c>/releaseprofile</c>, Sonarr's name).
/// </summary>
public static class IndexerOptionsEndpoints
{
    public const int MaxTagLength = 50;
    public const int MaxNameLength = 100;
    public const int MaxTerms = 200;
    public const int MaxScore = 10_000;

    public static IEndpointRouteBuilder MapIndexerOptionsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings/indexers").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        settings.MapGet("/", (IndexerOptions options) => IndexerOptionsResource.From(options.Values));

        settings.MapPut("/", (IndexerOptionsResource request, IndexerOptions options, ILogger<IndexerOptions> logger) =>
        {
            var errors = Validate(request, out var values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            options.Save(values!);
            logger.LogInformation(
                "Indexer settings saved: minimum age {MinimumAge} min, retention {Retention} days, maximum size {MaximumSize} MB, hardcoded subs {Hardcoded}, "
                + "minimum availability {Availability} (+{Delay} days), RSS sync every {Rss} min, missing search every {Missing} h, search on add {SearchOnAdd}",
                values!.MinimumAge, values.Retention, values.MaximumSize, values.AllowHardcodedSubs ? "allowed" : "rejected",
                values.MinimumAvailability, values.AvailabilityDelay, values.RssSyncInterval, values.MissingSearchInterval, values.SearchOnAdd);
            return Results.Ok(IndexerOptionsResource.From(options.Values));
        });

        var profiles = app.MapGroup("/api/v1/releaseprofile").WithTags("Release profiles").RequireAuthorization(AuthSchemes.AdminPolicy);

        profiles.MapGet("/", (ReleaseProfileRepository repo) => repo.All().Select(ReleaseProfileResource.From));

        profiles.MapGet("/{id:int}", (int id, ReleaseProfileRepository repo) =>
            repo.Find(id) is { } p ? Results.Ok(ReleaseProfileResource.From(p)) : Results.NotFound());

        profiles.MapPost("/", (ReleaseProfileResource request, ReleaseProfileRepository repo, IndexerRepository indexers, QualityProfileRepository qualityProfiles) =>
        {
            var errors = Validate(request, indexers, qualityProfiles, out var profile);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var id = repo.Add(profile!);
            return Results.Created($"/api/v1/releaseprofile/{id}", ReleaseProfileResource.From(repo.Find(id)!));
        });

        profiles.MapPut("/{id:int}", (int id, ReleaseProfileResource request, ReleaseProfileRepository repo, IndexerRepository indexers, QualityProfileRepository qualityProfiles) =>
        {
            if (repo.Find(id) is null)
            {
                return Results.NotFound();
            }

            var errors = Validate(request, indexers, qualityProfiles, out var profile);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            repo.Update(profile! with { Id = id });
            return Results.Ok(ReleaseProfileResource.From(repo.Find(id)!));
        });

        profiles.MapDelete("/{id:int}", (int id, ReleaseProfileRepository repo) => repo.Delete(id) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    /// <summary>
    /// "Start search for missing" after adding a movie or series: <paramref name="requested"/> from the add dialog, or the
    /// default from Settings → Indexers. Series are searched after the episode refresh that adding queues.
    /// </summary>
    public static void QueueSearchOnAdd(JobQueue jobs, IndexerOptions options, MediaType type, int id, string title, bool? requested)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(options);
        if (!(requested ?? options.SearchOnAdd))
        {
            return;
        }

        jobs.Enqueue(type == MediaType.Movie
            ? new Job($"search-new-movie:{id}", $"Search {title}", (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchNewMovieAsync(id, ct))
            : new Job($"search-missing-episodes:{id}", $"Search missing episodes of {title}", (sp, ct) => sp.GetRequiredService<AutomaticSearchService>().SearchMissingEpisodesAsync(id, ct)));
    }

    private static Dictionary<string, string[]> Validate(IndexerOptionsResource request, out IndexerOptionValues? values)
    {
        var errors = new Dictionary<string, string[]>();
        values = null;

        void Range(string field, int value, int min, int max, string unit)
        {
            if (value < min || value > max)
            {
                errors[field] = [$"Use {min} to {max} {unit}."];
            }
        }

        Range("minimumAge", request.MinimumAge, 0, IndexerOptions.MaxMinimumAge, "minutes");
        Range("retention", request.Retention, 0, IndexerOptions.MaxRetention, "days");
        Range("maximumSize", request.MaximumSize, 0, IndexerOptions.MaxMaximumSize, "MB");
        Range("availabilityDelay", request.AvailabilityDelay, -IndexerOptions.MaxAvailabilityDelay, IndexerOptions.MaxAvailabilityDelay, "days");

        if (request.RssSyncInterval != 0 && (request.RssSyncInterval < IndexerOptions.MinRssSyncInterval || request.RssSyncInterval > IndexerOptions.MaxRssSyncInterval))
        {
            errors["rssSyncInterval"] = [$"Use 0 (off) or {IndexerOptions.MinRssSyncInterval} to {IndexerOptions.MaxRssSyncInterval} minutes."];
        }

        if (request.MissingSearchInterval != 0
            && (request.MissingSearchInterval < IndexerOptions.MinMissingSearchInterval || request.MissingSearchInterval > IndexerOptions.MaxMissingSearchInterval))
        {
            errors["missingSearchInterval"] = [$"Use 0 (off) or {IndexerOptions.MinMissingSearchInterval} to {IndexerOptions.MaxMissingSearchInterval} hours."];
        }

        var tags = (request.WhitelistedHardcodedSubs ?? []).Select(t => t?.Trim() ?? string.Empty).ToList();
        if (tags.Any(t => t.Length == 0 || t.Length > MaxTagLength || t.Contains(',', StringComparison.Ordinal)))
        {
            errors["whitelistedHardcodedSubs"] = [$"Tags need 1 to {MaxTagLength} characters and no commas."];
        }

        if (!Enum.IsDefined(request.MinimumAvailability))
        {
            errors["minimumAvailability"] = ["Use announced, inCinemas or released."];
        }

        if (errors.Count == 0)
        {
            values = new IndexerOptionValues(
                request.MinimumAge,
                request.Retention,
                request.MaximumSize,
                request.AllowHardcodedSubs,
                [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)],
                request.MinimumAvailability,
                request.AvailabilityDelay,
                request.RssSyncInterval,
                request.MissingSearchInterval,
                request.SearchOnAdd);
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(
        ReleaseProfileResource request, IndexerRepository indexers, QualityProfileRepository qualityProfiles, out ReleaseProfile? profile)
    {
        var errors = new Dictionary<string, string[]>();
        profile = null;

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > MaxNameLength)
        {
            errors["name"] = [$"Use 1 to {MaxNameLength} characters."];
        }

        var required = Terms(request.Required);
        var ignored = Terms(request.Ignored);
        var preferred = (request.Preferred ?? []).Select(p => new PreferredTerm(p?.Term?.Trim() ?? string.Empty, p?.Score ?? 0)).ToList();

        CheckTerms(errors, "required", required);
        CheckTerms(errors, "ignored", ignored);
        CheckTerms(errors, "preferred", [.. preferred.Select(p => p.Term)]);
        if (!errors.ContainsKey("preferred") && preferred.FirstOrDefault(p => p.Score is < -MaxScore or > MaxScore or 0) is { } badScore)
        {
            errors["preferred"] = [$"'{badScore.Term}': use a score from -{MaxScore} to {MaxScore}, not 0."];
        }

        if (required.Count + ignored.Count + preferred.Count == 0)
        {
            errors["required"] = ["Add at least one term (must contain, must not contain or preferred)."];
        }

        var indexerIds = (request.IndexerIds ?? []).Distinct().ToList();
        var known = indexers.All().Select(i => i.Id).ToHashSet();
        if (indexerIds.Any(id => !known.Contains(id)))
        {
            errors["indexerIds"] = ["One of the indexers does not exist (any more)."];
        }

        var profileIds = (request.QualityProfileIds ?? []).Distinct().ToList();
        if (profileIds.Any(id => !qualityProfiles.Exists(id)))
        {
            errors["qualityProfileIds"] = ["One of the quality profiles does not exist (any more)."];
        }

        if (errors.Count == 0)
        {
            profile = new ReleaseProfile
            {
                Name = name,
                Enabled = request.Enabled,
                Required = required,
                Ignored = ignored,
                Preferred = preferred,
                IndexerIds = indexerIds,
                QualityProfileIds = profileIds,
            };
        }

        return errors;
    }

    private static List<string> Terms(IReadOnlyList<string?>? terms) =>
        [.. (terms ?? []).Select(t => t?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal)];

    private static void CheckTerms(Dictionary<string, string[]> errors, string field, IReadOnlyList<string> terms)
    {
        if (terms.Count > MaxTerms)
        {
            errors[field] = [$"Use at most {MaxTerms} terms."];
            return;
        }

        var problems = terms.Select(ReleaseTerms.Validate).OfType<string>().Distinct().ToArray();
        if (problems.Length > 0)
        {
            errors[field] = problems;
        }
    }
}

/// <summary>
/// Settings → Indexers. Ages in minutes (<see cref="MinimumAge"/>) or days, <see cref="MaximumSize"/> in MB, 0 = off or
/// unlimited. <see cref="RssSyncInterval"/> in minutes (0 = off), <see cref="MissingSearchInterval"/> in hours (0 = off).
/// </summary>
public sealed record IndexerOptionsResource(
    int MinimumAge,
    int Retention,
    int MaximumSize,
    bool AllowHardcodedSubs,
    IReadOnlyList<string>? WhitelistedHardcodedSubs,
    MinimumAvailability MinimumAvailability,
    int AvailabilityDelay,
    int RssSyncInterval,
    int MissingSearchInterval,
    bool SearchOnAdd)
{
    public static IndexerOptionsResource From(IndexerOptionValues v) => new(
        v.MinimumAge, v.Retention, v.MaximumSize, v.AllowHardcodedSubs, v.WhitelistedHardcodedSubs, v.MinimumAvailability, v.AvailabilityDelay,
        v.RssSyncInterval, v.MissingSearchInterval, v.SearchOnAdd);
}

public sealed record PreferredTermResource(string? Term, int Score);

/// <summary>A release profile. Empty <see cref="IndexerIds"/> or <see cref="QualityProfileIds"/> = all.</summary>
public sealed record ReleaseProfileResource(
    int Id,
    string? Name,
    bool Enabled,
    IReadOnlyList<string?>? Required,
    IReadOnlyList<string?>? Ignored,
    IReadOnlyList<PreferredTermResource?>? Preferred,
    IReadOnlyList<int>? IndexerIds,
    IReadOnlyList<int>? QualityProfileIds)
{
    public static ReleaseProfileResource From(ReleaseProfile p) => new(
        p.Id, p.Name, p.Enabled, p.Required, p.Ignored, [.. p.Preferred.Select(t => new PreferredTermResource(t.Term, t.Score))], p.IndexerIds, p.QualityProfileIds);
}
