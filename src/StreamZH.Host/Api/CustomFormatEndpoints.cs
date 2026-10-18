// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.CustomFormats;
using StreamZH.Parsing.Releases;

namespace StreamZH.Host.Api;

/// <summary>
/// Custom formats like Radarr's and Sonarr's Settings → Custom Formats: list, edit, condition schema per media type, import
/// and export of Radarr/Sonarr/TRaSH JSON, and a test that shows which formats a release name matches. Reads for every
/// user, changes for admins.
/// </summary>
public static class CustomFormatEndpoints
{
    public static IEndpointRouteBuilder MapCustomFormatEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/customformat").WithTags("Custom formats").RequireAuthorization();

        api.MapGet("/", (string? mediaType, CustomFormatService service) =>
        {
            var type = ParseMediaType(mediaType);
            var usage = service.UsageCounts();
            var all = type is { } t ? service.ForMediaType(t) : service.All();
            return all.Select(f => CustomFormatResource.From(f, usage));
        });

        api.MapGet("/schema", (string? mediaType) =>
        {
            var type = ParseMediaType(mediaType) ?? MediaType.Movie;
            return CustomFormatSchema.For(type);
        });

        api.MapGet("/{id:int}", (int id, CustomFormatService service) =>
            service.Find(id) is { } format ? Results.Ok(CustomFormatResource.From(format, service.UsageCounts())) : Results.NotFound());

        api.MapGet("/{id:int}/export", (int id, CustomFormatService service) =>
            service.Find(id) is { } format ? Results.Json(CustomFormatService.Export(format)) : Results.NotFound());

        api.MapPost("/", (CustomFormatRequest request, CustomFormatService service) =>
        {
            var created = service.Create(request.ToFormat());
            return Results.Created($"/api/v1/customformat/{created.Id}", CustomFormatResource.From(created, service.UsageCounts()));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPut("/{id:int}", (int id, CustomFormatRequest request, CustomFormatService service) =>
        {
            var existing = service.Find(id) ?? throw new LibraryException("Custom format not found.", LibraryError.NotFound);
            var saved = service.Update(id, request.ToFormat(existing.MediaType));
            return CustomFormatResource.From(saved, service.UsageCounts());
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapDelete("/{id:int}", (int id, CustomFormatService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/import", (CustomFormatImportRequest request, CustomFormatService service) =>
        {
            var type = ParseMediaType(request.MediaType) ?? throw Invalid("mediaType", "Use movie or series.");
            var json = request.Json is { ValueKind: JsonValueKind.String } text ? text.GetString() : request.Json?.GetRawText();
            var imported = service.Import(type, json);
            var usage = service.UsageCounts();
            return imported.Select(f => CustomFormatResource.From(f, usage));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapPost("/test", (CustomFormatTestRequest request, CustomFormatService service, QualityProfileRepository profiles) =>
            Test(request, service, profiles));

        return app;
    }

    /// <summary>"movie"/"series" (any case, also "movies"); null when empty or unknown.</summary>
    public static MediaType? ParseMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "series" or "tv" or "episode" => MediaType.Series,
        _ => null,
    };

    private static CustomFormatTestResult Test(CustomFormatTestRequest request, CustomFormatService service, QualityProfileRepository profiles)
    {
        var errors = new Dictionary<string, string[]>();
        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            errors["title"] = ["Enter a release name."];
        }

        var type = ParseMediaType(request.MediaType);
        if (type is null)
        {
            errors["mediaType"] = ["Use movie or series."];
        }

        if (request.SizeGb is < 0 or > 100_000)
        {
            errors["sizeGb"] = ["Enter a size from 0 to 100000 GB."];
        }

        var profile = request.QualityProfileId is { } pid ? profiles.Find(pid) : null;
        if (request.QualityProfileId is not null && profile is null)
        {
            errors["qualityProfileId"] = ["This quality profile does not exist."];
        }

        var original = request.OriginalLanguageId is { } lid ? Language.FindById(lid) : Language.English;
        if (request.OriginalLanguageId is { } invalid && (invalid <= 0 || original.Id != invalid))
        {
            errors["originalLanguageId"] = ["Unknown language."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var mediaType = type!.Value;
        var parsed = (mediaType == MediaType.Series ? ReleaseParser.ParseEpisode(title) : ReleaseParser.ParseMovie(title))
            ?? ReleaseParser.Parse(title)
            ?? new ParsedRelease { ReleaseTitle = title, Title = title, CleanTitle = string.Empty };
        var size = (long)Math.Round((request.SizeGb ?? 0) * 1024 * 1024 * 1024);
        var input = CustomFormatInput.ForRelease(parsed, size, mediaType, original);

        var formats = service.ForMediaType(mediaType)
            .Select(f => CustomFormatCalculator.Evaluate(f, input))
            .Select(e => new CustomFormatTestFormat(
                e.Format.Id,
                e.Format.Name,
                e.Matched,
                profile is null ? null : CustomFormatCalculator.ScoreOf(profile, e.Format.Id),
                [.. e.Specifications.Select(s => new CustomFormatTestSpecification(s.Specification.Name, s.Specification.Implementation, s.Matched))]))
            .OrderByDescending(f => f.Matched)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = formats.Where(f => f.Matched).Sum(f => f.Score ?? 0);
        return new CustomFormatTestResult(CustomFormatTestParsed.From(parsed, input), formats, total);
    }

    private static ValidationFailedException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>One condition; <see cref="Fields"/> holds <c>value</c>, <c>min</c>/<c>max</c> and, for languages, <c>exceptLanguage</c>.</summary>
public sealed record CustomFormatSpecificationResource(string Name, string Implementation, bool Negate, bool Required, IReadOnlyDictionary<string, JsonElement> Fields);

/// <summary>A custom format. <see cref="UsedBy"/>: how many quality profiles give it a non-zero score.</summary>
public sealed record CustomFormatResource(
    int Id,
    string Name,
    string MediaType,
    bool IncludeInRename,
    IReadOnlyList<CustomFormatSpecificationResource> Specifications,
    string? TrashId,
    int UsedBy)
{
    public static CustomFormatResource From(CustomFormat format, IReadOnlyDictionary<int, int> usage)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(usage);
        return new(
            format.Id,
            format.Name,
            QualityProfileResource.MediaTypeName(format.MediaType),
            format.IncludeInRename,
            [.. format.Specifications.Select(s => new CustomFormatSpecificationResource(s.Name, s.Implementation, s.Negate, s.Required, s.Fields))],
            format.TrashId,
            usage.GetValueOrDefault(format.Id));
    }
}

public sealed record CustomFormatSpecificationRequest(string? Name, string? Implementation, bool Negate, bool Required, Dictionary<string, JsonElement>? Fields);

/// <summary>Body for creating or updating a format; the media type of an existing format cannot change.</summary>
public sealed record CustomFormatRequest(
    string? Name,
    string? MediaType,
    bool IncludeInRename,
    IReadOnlyList<CustomFormatSpecificationRequest>? Specifications,
    string? TrashId)
{
    /// <param name="existingType">The stored media type on an update; the body may leave it out.</param>
    public CustomFormat ToFormat(MediaType? existingType = null)
    {
        var type = CustomFormatEndpoints.ParseMediaType(MediaType) ?? existingType;
        if (type is null)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["mediaType"] = ["Use movie or series."] });
        }

        return new CustomFormat
        {
            Name = Name ?? string.Empty,
            MediaType = type.Value,
            IncludeInRename = IncludeInRename,
            TrashId = TrashId,
            Specifications =
            [
                .. (Specifications ?? []).Select(s => new CustomFormatSpecification
                {
                    Name = s.Name ?? string.Empty,
                    Implementation = s.Implementation ?? string.Empty,
                    Negate = s.Negate,
                    Required = s.Required,
                    Fields = s.Fields ?? [],
                }),
            ],
        };
    }
}

/// <summary><see cref="Json"/>: the pasted JSON as a string (an object or array is accepted too).</summary>
public sealed record CustomFormatImportRequest(string? MediaType, JsonElement? Json);

/// <summary>
/// Test body. <see cref="OriginalLanguageId"/> is what "Original" language conditions compare against (English when left out).
/// </summary>
public sealed record CustomFormatTestRequest(string? Title, string? MediaType, double? SizeGb, int? QualityProfileId, int? OriginalLanguageId = null);

public sealed record CustomFormatTestSpecification(string Name, string Implementation, bool Matched);

/// <summary>One format in the test; <see cref="Score"/> is the profile's score for it (null without a profile).</summary>
public sealed record CustomFormatTestFormat(int Id, string Name, bool Matched, int? Score, IReadOnlyList<CustomFormatTestSpecification> Specifications);

public sealed record CustomFormatTestResult(CustomFormatTestParsed Parsed, IReadOnlyList<CustomFormatTestFormat> Formats, int TotalScore);

/// <summary>What the conditions see: quality parts in the media type's values, effective languages, release type for series.</summary>
public sealed record CustomFormatTestParsed(
    string Title,
    int Year,
    string Quality,
    string Source,
    int Resolution,
    string Modifier,
    int Version,
    int Real,
    IReadOnlyList<string> Languages,
    string? ReleaseGroup,
    string? Edition,
    string? ReleaseType,
    int? SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers)
{
    public static CustomFormatTestParsed From(ParsedRelease parsed, CustomFormatInput input)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(input);
        var quality = input.Quality.Quality;
        var source = CustomFormatValues.Source(input.MediaType, quality);
        var series = input.MediaType == MediaType.Series;
        return new(
            parsed.Title,
            input.Year,
            quality.Name,
            CustomFormatValues.Sources(input.MediaType).FirstOrDefault(o => o.Value == source)?.Label ?? "Unknown",
            CustomFormatValues.Resolution(input.MediaType, quality),
            CustomFormatValues.Modifiers.FirstOrDefault(o => o.Value == CustomFormatValues.Modifier(quality))?.Label ?? "None",
            input.Quality.Revision.Version,
            input.Quality.Revision.Real,
            [.. input.Languages.Select(l => l.Name)],
            input.ReleaseGroup,
            input.Edition,
            series ? CustomFormatValues.ReleaseTypes.First(o => o.Value == (int)input.ReleaseType).Label : null,
            parsed.SeasonNumber,
            parsed.EpisodeNumbers);
    }
}
