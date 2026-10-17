// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;
using StreamZH.Host.Auth;
using StreamZH.Library;

namespace StreamZH.Host.Api;

/// <summary>Qualities, languages, quality profiles and size limits. Reads for every user, changes for admins.</summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/qualities", () => Quality.All.Select(QualityResource.From)).WithTags("Qualities");
        api.MapGet("/languages", () => new[] { Language.Any, Language.Original }.Concat(Language.All).Select(LanguageResource.From))
            .WithTags("Languages");

        var profiles = api.MapGroup("/qualityprofiles").WithTags("Quality profiles");
        profiles.MapGet("/", (QualityProfileService service) => service.All().Select(QualityProfileResource.From));
        profiles.MapGet("/schema", () => QualityProfileResource.From(QualityProfileService.Schema()));
        profiles.MapGet("/{id:int}", (int id, QualityProfileService service) =>
            service.Find(id) is { } p ? Results.Ok(QualityProfileResource.From(p)) : Results.NotFound());
        profiles.MapPost("/", (QualityProfileRequest request, QualityProfileService service) =>
        {
            var created = service.Create(request.ToProfile());
            return Results.Created($"/api/v1/qualityprofiles/{created.Id}", QualityProfileResource.From(created));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        profiles.MapPut("/{id:int}", (int id, QualityProfileRequest request, QualityProfileService service) =>
            QualityProfileResource.From(service.Update(id, request.ToProfile())))
            .RequireAuthorization(AuthSchemes.AdminPolicy);
        profiles.MapDelete("/{id:int}", (int id, QualityProfileService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        var definitions = api.MapGroup("/qualitydefinitions").WithTags("Quality definitions");
        definitions.MapGet("/", (QualityDefinitionService service) => service.All());
        definitions.MapPut("/", (IReadOnlyList<QualityDefinitionRequest> request, QualityDefinitionService service) =>
            service.Save([.. request.Select(r => r.ToDefinition())]))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        // Readable for every user, so the add dialog can preselect the default profile.
        var settings = api.MapGroup("/settings/profiles").WithTags("Settings");
        settings.MapGet("/", (QualityProfileService service) => new ProfileSettings(service.DefaultProfileId()));
        settings.MapPut("/", (ProfileSettings request, QualityProfileService service) =>
        {
            service.SetDefault(request.DefaultQualityProfileId);
            return new ProfileSettings(service.DefaultProfileId());
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }
}
