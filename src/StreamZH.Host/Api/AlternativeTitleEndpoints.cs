// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Titles;
using StreamZH.Parsing.Releases;

namespace StreamZH.Host.Api;

/// <summary>
/// Alternative titles of a movie or series ("Also known as"): <c>/api/v1/movies/{id}/titles</c> and
/// <c>/api/v1/series/{id}/titles</c>. Reading is open to every user; adding and removing your own titles needs an admin.
/// <c>/api/v1/library/match</c> tells which library item a release name belongs to.
/// </summary>
public static class AlternativeTitleEndpoints
{
    public static IEndpointRouteBuilder MapAlternativeTitleEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Alternative titles");

        api.MapGet("/movies/{id:int}/titles", (int id, MovieRepository movies, AlternativeTitleService titles) =>
        {
            _ = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
            return titles.For(MediaType.Movie, id).Select(AlternativeTitleResource.From);
        });
        api.MapPost("/movies/{id:int}/titles", (int id, AddAlternativeTitleRequest request, MovieRepository movies, AlternativeTitleService titles) =>
        {
            var movie = movies.Find(id) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound);
            var added = titles.AddUserTitle(MediaType.Movie, id, movie.Title, movie.OriginalTitle, request.Title, request.SeasonNumber);
            return Results.Created($"/api/v1/movies/{id}/titles/{added.Id}", AlternativeTitleResource.From(added));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        api.MapDelete("/movies/{id:int}/titles/{titleId:int}", (int id, int titleId, AlternativeTitleService titles) =>
        {
            titles.DeleteUserTitle(MediaType.Movie, id, titleId);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        api.MapGet("/series/{id:int}/titles", (int id, SeriesRepository series, AlternativeTitleService titles) =>
        {
            _ = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
            return titles.For(MediaType.Series, id).Select(AlternativeTitleResource.From);
        });
        api.MapPost("/series/{id:int}/titles", (int id, AddAlternativeTitleRequest request, SeriesRepository series, AlternativeTitleService titles) =>
        {
            var show = series.Find(id) ?? throw new LibraryException("Series not found.", LibraryError.NotFound);
            var added = titles.AddUserTitle(MediaType.Series, id, show.Title, show.OriginalTitle, request.Title, request.SeasonNumber);
            return Results.Created($"/api/v1/series/{id}/titles/{added.Id}", AlternativeTitleResource.From(added));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);
        api.MapDelete("/series/{id:int}/titles/{titleId:int}", (int id, int titleId, AlternativeTitleService titles) =>
        {
            titles.DeleteUserTitle(MediaType.Series, id, titleId);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Which library item a release or download name belongs to (by any of its titles); the manual import preselects it.
        api.MapGet("/library/match", (string? title, MovieRepository movies, SeriesRepository series, AlternativeTitleRepository alternativeTitles) =>
        {
            var parsed = string.IsNullOrWhiteSpace(title) ? null : ReleaseParser.Parse(title.Trim());
            return new LibraryMatchAnswer(parsed is null ? null : LibraryMatchResource.Find(parsed, movies, series, alternativeTitles));
        }).WithTags("Library");

        return app;
    }
}

public sealed record LibraryMatchAnswer(LibraryMatchResource? Match);

/// <summary>Your own title; <see cref="SeasonNumber"/> limits it to one season of a series.</summary>
public sealed record AddAlternativeTitleRequest(string? Title, int? SeasonNumber);

/// <param name="Source">tmdb, translation, xem or user.</param>
/// <param name="Language">Language name (e.g. "German"), null when unknown.</param>
public sealed record AlternativeTitleResource(int Id, string Title, string Source, string? Language, int? SeasonNumber, int? SceneSeasonNumber)
{
    public static AlternativeTitleResource From(AlternativeTitle t) => new(
        t.Id, t.Title, t.Source, t.LanguageId is { } l && l > 0 ? Core.Quality.Language.FindById(l).Name : null, t.SeasonNumber, t.SceneSeasonNumber);
}
