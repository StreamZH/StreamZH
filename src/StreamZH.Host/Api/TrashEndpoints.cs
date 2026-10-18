// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Trash;

namespace StreamZH.Host.Api;

/// <summary>
/// TRaSH Guides (Recyclarr inside StreamZH): status and sync of the local copy, the guide's profiles, formats and groups,
/// applying templates to a quality profile, importing a recyclarr.yml and the sync settings. Reads for every user,
/// changes for admins. The lists read only the local copy; apply and the Recyclarr import download it first if needed.
/// </summary>
public static class TrashEndpoints
{
    public static IServiceCollection AddTrash(this IServiceCollection services) => services.AddTrashGuides();

    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        var group = api.MapGroup("/trash").WithTags("TRaSH Guides");

        group.MapGet("/status", (TrashGuideStore store, TrashSettings settings) => Status(store, settings));

        group.MapPost("/sync", async (TrashSyncService sync, TrashGuideStore store, TrashSettings settings, CancellationToken ct) =>
        {
            await sync.SyncAsync(ct);
            return Status(store, settings);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/profiles", (string? mediaType, TrashGuideStore store) =>
            Guides(mediaType, store, (guide, _) => guide.Profiles.Select(p => TrashProfileSummary.From(p, guide.MediaType))));

        group.MapGet("/profiles/{trashId}", (string trashId, string? mediaType, TrashGuideStore store, CustomFormatRepository formats) =>
        {
            if (ParseMediaTypes(mediaType) is not { } types)
            {
                return MediaTypeProblem();
            }

            var guides = store.Current;
            foreach (var type in types)
            {
                if (guides.For(type).Profile(trashId) is { } profile)
                {
                    return Results.Ok(TrashProfileDetail.From(profile, guides.For(type), formats));
                }
            }

            return Results.NotFound();
        });

        group.MapGet("/formats", (string? mediaType, TrashGuideStore store, CustomFormatRepository formats) =>
        {
            var installed = formats.All().Where(f => f.TrashId is not null).Select(f => (f.MediaType, f.TrashId!.ToLowerInvariant())).ToHashSet();
            return Guides(mediaType, store, (guide, _) => guide.CustomFormats.Select(f => TrashFormatResource.From(f, guide, installed)));
        });

        group.MapGet("/groups", (string? mediaType, TrashGuideStore store) =>
            Guides(mediaType, store, (guide, _) => guide.Groups.Select(g => TrashGroupResource.From(g, guide.MediaType))));

        group.MapPost("/apply", async (
            TrashApplyBody body, TrashGuideStore store, TrashApplyService apply, TrashRepository trash, CancellationToken ct) =>
        {
            await EnsureCopyAsync(store, ct);
            var previous = body.ProfileId is { } id ? trash.Options(id) : null;

            // An explicit apply follows the templates again: hand-set fields are reset, imported Recyclarr extras stay.
            var options = new TrashProfileOptions
            {
                ScoreSet = previous?.ScoreSet,
                SkipGroups = previous?.SkipGroups ?? [],
                ExtraFormats = previous?.ExtraFormats ?? [],
                ResetUnmatched = previous?.ResetUnmatched ?? false,
                ResetExcept = previous?.ResetExcept ?? [],
                AppliedFormatIds = previous?.AppliedFormatIds ?? [],
                Groups = [.. (body.ExtraGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => new TrashGroupSelection { TrashId = g.Trim() })],
            };
            var result = apply.ApplyWithExtras(
                new TrashApplyRequest
                {
                    ProfileId = body.ProfileId,
                    Name = body.Name,
                    MovieTemplate = body.MovieTemplate,
                    SeriesTemplate = body.SeriesTemplate,
                    Options = options,
                },
                body.ApplyQualitySizes,
                body.Naming?.ToNaming(),
                store.Current);
            return Results.Ok(new TrashApplyResultResource(
                QualityProfileResource.From(result.Profile), result.ProfileCreated, result.Created, result.Updated, result.Warnings));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapPost("/recyclarr", async (RecyclarrBody body, TrashGuideStore store, RecyclarrImporter importer, CancellationToken ct) =>
        {
            await EnsureCopyAsync(store, ct);
            return Results.Ok(importer.Run(body.Yaml ?? string.Empty, body.Apply, store.Current));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        var settings = api.MapGroup("/settings/trash").WithTags("Settings");
        settings.MapGet("/", (TrashSettings trashSettings) => trashSettings.Values);
        settings.MapPut("/", (TrashSettingsBody body, TrashSettings trashSettings, ILogger<TrashSettings> logger) =>
        {
            var values = body.ToValues();
            var errors = TrashSettings.Validate(values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            trashSettings.Save(values);
            logger.LogInformation(
                "TRaSH settings saved: {State}, {Repository} ({Branch}), every {Hours} h, auto update {Auto}, keep overrides {Keep}",
                values.Enabled ? "on" : "off", values.Repository, values.Branch, values.SyncIntervalHours, values.AutoUpdate, values.KeepScoreOverrides);
            return Results.Ok(trashSettings.Values);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    private static TrashStatusResource Status(TrashGuideStore store, TrashSettings settings)
    {
        var values = settings.Values;
        var guides = store.Current;
        return new TrashStatusResource(
            values.Enabled,
            values.Repository,
            values.Branch,
            store.LastSync,
            store.HasCopy ? store.Commit : null,
            store.LastError,
            new TrashCounts(guides.Movie.CustomFormats.Count, guides.Series.CustomFormats.Count, guides.Movie.Profiles.Count, guides.Series.Profiles.Count));
    }

    /// <summary>Downloads the guides when there is no copy yet; a failed download leaves an error the apply then reports.</summary>
    private static async Task EnsureCopyAsync(TrashGuideStore store, CancellationToken ct)
    {
        if (!store.HasCopy)
        {
            var result = await store.DownloadAsync(ct);
            if (!store.HasCopy)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]>
                {
                    ["guides"] = [result.Error ?? "The TRaSH Guides could not be downloaded."],
                });
            }
        }
    }

    private static IResult Guides<T>(string? mediaType, TrashGuideStore store, Func<TrashGuide, MediaType, IEnumerable<T>> select)
    {
        if (ParseMediaTypes(mediaType) is not { } types)
        {
            return MediaTypeProblem();
        }

        var guides = store.Current;
        return Results.Ok(types.SelectMany(t => select(guides.For(t), t)).ToList());
    }

    /// <summary>"movie", "series" or nothing (both); null when the value is unknown.</summary>
    private static MediaType[]? ParseMediaTypes(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        null or "" => [MediaType.Movie, MediaType.Series],
        "movie" or "movies" or "radarr" => [MediaType.Movie],
        "series" or "tv" or "sonarr" => [MediaType.Series],
        _ => null,
    };

    private static IResult MediaTypeProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });

    internal static string Label(MediaType mediaType) => mediaType == MediaType.Series ? "series" : "movie";
}

public sealed record TrashCounts(int MovieFormats, int SeriesFormats, int MovieProfiles, int SeriesProfiles);

public sealed record TrashStatusResource(
    bool Enabled, string Repository, string Branch, DateTimeOffset? LastSync, string? Commit, string? Error, TrashCounts Counts);

public sealed record TrashProfileSummary(
    string TrashId, string MediaType, string Name, string Slug, string? Group, string? Description, string? Url, string? Language)
{
    public static TrashProfileSummary From(TrashQualityProfile p, MediaType mediaType) =>
        new(p.TrashId, TrashEndpoints.Label(mediaType), p.Name, p.Slug, p.Group, p.Description, p.Url, p.Language);
}

public sealed record TrashQualityItemResource(string Name, bool Allowed, IReadOnlyList<string> Qualities, IReadOnlyList<int> QualityIds);

/// <param name="Source">"profile" (the template's formats) or "group" (a default CF group of the template).</param>
public sealed record TrashProfileFormatResource(string TrashId, string Name, int Score, string Source, string? Group, bool Installed);

/// <param name="Default">Added automatically to this template.</param>
public sealed record TrashProfileGroupResource(string TrashId, string Name, bool Default);

public sealed record TrashProfileDetail(
    string TrashId,
    string MediaType,
    string Name,
    string Slug,
    string? Group,
    string? Description,
    string? Url,
    string? Language,
    string ScoreSet,
    bool UpgradeAllowed,
    string? Cutoff,
    int MinFormatScore,
    int CutoffFormatScore,
    int MinUpgradeFormatScore,
    IReadOnlyList<TrashQualityItemResource> Qualities,
    IReadOnlyList<TrashProfileFormatResource> Formats,
    IReadOnlyList<TrashProfileGroupResource> Groups)
{
    public static TrashProfileDetail From(TrashQualityProfile p, TrashGuide guide, CustomFormatRepository repository)
    {
        var scoreSet = p.ScoreSet ?? TrashGuide.DefaultScoreSet;
        var installed = repository.ForMediaType(guide.MediaType).Where(f => f.TrashId is not null)
            .Select(f => f.TrashId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var formats = new List<TrashProfileFormatResource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in p.FormatItems)
        {
            if (guide.Format(item.Value) is { } format && seen.Add(format.TrashId))
            {
                formats.Add(new TrashProfileFormatResource(format.TrashId, format.Name, format.ScoreFor(scoreSet) ?? 0, "profile", null, installed.Contains(format.TrashId)));
            }
        }

        var groups = guide.Groups.Select(g => new TrashProfileGroupResource(g.TrashId, g.Name, g.Default && g.AppliesTo(p.TrashId))).ToList();
        foreach (var group in guide.Groups.Where(g => g.Default && g.AppliesTo(p.TrashId)))
        {
            foreach (var member in group.Formats.Where(f => f.Required || f.Default))
            {
                if (guide.Format(member.TrashId) is { } format && seen.Add(format.TrashId))
                {
                    formats.Add(new TrashProfileFormatResource(
                        format.TrashId, format.Name, format.ScoreFor(scoreSet) ?? 0, "group", group.Name, installed.Contains(format.TrashId)));
                }
            }
        }

        return new TrashProfileDetail(
            p.TrashId,
            TrashEndpoints.Label(guide.MediaType),
            p.Name,
            p.Slug,
            p.Group,
            p.Description,
            p.Url,
            p.Language,
            scoreSet,
            p.UpgradeAllowed,
            p.Cutoff,
            p.MinFormatScore,
            p.CutoffFormatScore,
            p.MinUpgradeFormatScore,
            [.. p.Items.Select(i => new TrashQualityItemResource(
                i.Name,
                i.Allowed,
                i.Qualities,
                [.. (i.IsGroup ? i.Qualities : [i.Name]).Select(n => TrashQualityNames.Find(n, guide.MediaType)?.Id).OfType<int>()]))],
            formats,
            groups);
    }
}

/// <param name="DefaultScore">Score of the "default" set (null when the guide has none).</param>
/// <param name="Groups">Names of the CF groups the format is in.</param>
public sealed record TrashFormatResource(
    string TrashId,
    string MediaType,
    string Name,
    string? Description,
    int? DefaultScore,
    IReadOnlyDictionary<string, int> Scores,
    IReadOnlyList<string> Groups,
    string? Category,
    bool Installed)
{
    public static TrashFormatResource From(TrashCustomFormat f, TrashGuide guide, HashSet<(MediaType, string)> installed) => new(
        f.TrashId,
        TrashEndpoints.Label(guide.MediaType),
        f.Name,
        f.Description,
        f.Scores.TryGetValue(TrashGuide.DefaultScoreSet, out var score) ? score : null,
        f.Scores,
        [.. guide.Groups.Where(g => g.Formats.Any(x => string.Equals(x.TrashId, f.TrashId, StringComparison.OrdinalIgnoreCase))).Select(g => g.Name)],
        f.Category,
        installed.Contains((guide.MediaType, f.TrashId.ToLowerInvariant())));
}

public sealed record TrashGroupFormatResource(string TrashId, string Name, bool Required, bool Default);

/// <param name="Default">Added automatically to the profiles in <see cref="Include"/> (all but <see cref="Exclude"/> when empty).</param>
public sealed record TrashGroupResource(
    string TrashId, string MediaType, string Name, string? Description, bool Default, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude,
    IReadOnlyList<TrashGroupFormatResource> Formats)
{
    public static TrashGroupResource From(TrashCustomFormatGroup g, MediaType mediaType) => new(
        g.TrashId,
        TrashEndpoints.Label(mediaType),
        g.Name,
        g.Description,
        g.Default,
        g.Include,
        g.Exclude,
        [.. g.Formats.Select(f => new TrashGroupFormatResource(f.TrashId, f.Name, f.Required, f.Default))]);
}

public sealed record TrashNamingBody(string? MovieFolder, string? MovieFile, string? SeriesFolder, string? SeriesFile)
{
    public TrashNaming ToNaming() => new(MovieFolder, MovieFile, SeriesFolder, SeriesFile);
}

public sealed record TrashApplyBody(
    int? ProfileId,
    string? Name,
    string? MovieTemplate,
    string? SeriesTemplate,
    IReadOnlyList<string>? ExtraGroups,
    bool ApplyQualitySizes = false,
    TrashNamingBody? Naming = null);

/// <param name="Created">Custom formats installed.</param>
/// <param name="Updated">Custom formats updated.</param>
public sealed record TrashApplyResultResource(
    QualityProfileResource Profile, bool ProfileCreated, int Created, int Updated, IReadOnlyList<string> Warnings);

public sealed record RecyclarrBody(string? Yaml, bool Apply = false);

/// <summary>Missing fields keep the defaults.</summary>
public sealed record TrashSettingsBody(
    bool? Enabled, string? Repository, string? Branch, int? SyncIntervalHours, bool? AutoUpdate, bool? KeepScoreOverrides)
{
    public TrashSettingsValues ToValues() => new(
        Enabled ?? TrashSettings.Defaults.Enabled,
        Repository ?? TrashSettings.DefaultRepository,
        Branch ?? TrashSettings.DefaultBranch,
        SyncIntervalHours ?? TrashSettings.DefaultSyncIntervalHours,
        AutoUpdate ?? TrashSettings.Defaults.AutoUpdate,
        KeepScoreOverrides ?? TrashSettings.Defaults.KeepScoreOverrides);
}
