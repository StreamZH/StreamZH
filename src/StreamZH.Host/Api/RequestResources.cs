// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Host.Auth;
using StreamZH.Library.Requests;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>API names (camelCase strings) of the request enums and the actor of an HTTP request (0.10).</summary>
public static class RequestApi
{
    public static string Status(RequestStatus status) => JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    public static string Source(RequestSource source) => JsonNamingPolicy.CamelCase.ConvertName(source.ToString());

    public static string Type(MediaType mediaType) => mediaType == MediaType.Movie ? "movie" : "series";

    public static bool TryStatus(string? text, out RequestStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(text)
            && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && Enum.TryParse(text.Trim(), ignoreCase: true, out status)
            && Enum.IsDefined(status);
    }

    /// <summary>"movie"/"movies" or "series"/"tv".</summary>
    public static bool TryType(string? text, out MediaType mediaType)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "movie" or "movies":
                mediaType = MediaType.Movie;
                return true;
            case "series" or "tv" or "show" or "shows":
                mediaType = MediaType.Series;
                return true;
            default:
                mediaType = default;
                return false;
        }
    }

    /// <summary>The signed-in user (id 0 = API key or a login-free local address: owner rights without an account).</summary>
    public static RequestActor Actor(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        int? id = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        var role = Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : UserRole.Requester;
        var source = user.Identity?.AuthenticationType == AuthSchemes.ApiKey ? RequestSource.Api : RequestSource.Web;
        return new RequestActor(id, role, source);
    }

    /// <summary>"/api/v1/mediacover/w342/abc.jpg" (StreamZH's image cache) for a valid TMDB path, else null.</summary>
    public static string? Image(string? path, string size) =>
        TmdbImages.IsValidPath(path) ? $"/api/v1/mediacover/{size}{path}" : null;

    /// <summary>The HTTP answer of a refused request action.</summary>
    public static IResult Problem(RequestException ex, Func<MediaRequest, RequestResource>? resource = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Error switch
        {
            RequestError.Invalid or RequestError.LimitReached => Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.Field ?? "request"] = [ex.Message] },
                title: ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Error == RequestError.LimitReached ? "limit-reached" : "invalid" }),
            RequestError.NotFound => Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound, title: ex.Message, extensions: Code("not-found")),
            RequestError.Forbidden => Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden, title: ex.Message, extensions: Code("forbidden")),
            RequestError.Disabled => Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: ex.Message, extensions: Code("requests-disabled")),
            RequestError.AlreadyAvailable => Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: ex.Message, extensions: Code("already-available")),
            RequestError.AlreadyRequested => Results.Problem(
                ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: ex.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "already-requested",
                    ["request"] = ex.Existing is { } existing && resource is not null ? resource(existing) : null,
                }),
            _ => Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: ex.Message, extensions: Code("conflict")),
        };
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}

/// <summary>A request as the API shows it. Dates are UTC.</summary>
/// <param name="Seasons">Season numbers; null = all seasons (or a movie).</param>
/// <param name="RequestedBy">The StreamZH user id; null for Discord guests and API requests.</param>
/// <param name="RequestedByName">Display name of the requester (admins, and the requester on their own requests).</param>
/// <param name="DecidedByName">Who approved or declined it; only admins see it.</param>
public sealed record RequestResource(
    int Id,
    string MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? PosterPath,
    string? PosterUrl,
    IReadOnlyList<int>? Seasons,
    string Status,
    string Source,
    int? RequestedBy,
    string? RequestedByName,
    string? DiscordUserId,
    string? DiscordName,
    int? QualityProfileId,
    int? RootFolderId,
    int? ItemId,
    string? Note,
    string? Reason,
    int? DecidedBy,
    string? DecidedByName,
    DateTime Created,
    DateTime Updated,
    DateTime? Decided,
    DateTime? Available)
{
    /// <param name="viewer">
    /// Who reads it: names of other users are only shown to admins; somebody else's request (the open request a duplicate
    /// points to) shows only the title and its state.
    /// </param>
    public static RequestResource From(MediaRequest r, IReadOnlyDictionary<int, string> names, RequestActor viewer)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(viewer);
        if (!RequestService.CanSee(viewer, r))
        {
            r = r with
            {
                RequestedBy = null, DiscordUserId = null, DiscordName = null, Note = null, Reason = null, DecidedBy = null, QualityProfileId = null,
                RootFolderId = null,
            };
        }

        string? Name(int? id) => id is { } value && (viewer.IsAdmin || value == viewer.UserId) ? names.GetValueOrDefault(value) : null;
        return new RequestResource(
            r.Id,
            RequestApi.Type(r.MediaType),
            r.TmdbId,
            r.Title,
            r.Year,
            r.PosterPath,
            RequestApi.Image(r.PosterPath, "w342"),
            r.Seasons,
            RequestApi.Status(r.Status),
            RequestApi.Source(r.Source),
            r.RequestedBy,
            Name(r.RequestedBy) ?? (r.RequestedBy is null && r.DiscordName is { } discord ? discord : null),
            r.DiscordUserId,
            r.DiscordName,
            r.QualityProfileId,
            r.RootFolderId,
            r.ItemId,
            r.Note,
            r.Reason,
            r.DecidedBy,
            Name(r.DecidedBy),
            r.Created,
            r.Updated,
            r.Decided,
            r.Available);
    }
}

public sealed record RequestPageResource(int Page, int PageSize, int TotalRecords, IReadOnlyList<RequestResource> Records);

/// <summary>Body of POST /api/v1/requests. <see cref="Seasons"/>: an array of season numbers, "all" or null (all).</summary>
public sealed record CreateRequestBody(string? MediaType, int TmdbId, JsonElement? Seasons = null, string? Note = null);

public sealed record ApproveRequestBody(int? QualityProfileId = null, int? RootFolderId = null, IReadOnlyList<int>? Seasons = null);

public sealed record DeclineRequestBody(string? Reason = null);

/// <summary>Library and request state of a title for Discover badges.</summary>
public sealed record TitleStateResource(bool InLibrary, bool Available, bool PartiallyAvailable, int? ItemId, string? Requested, int? RequestId)
{
    public static TitleStateResource From(TitleState s) =>
        new(s.InLibrary, s.Available, s.PartiallyAvailable, s.ItemId, s.Requested is { } status ? RequestApi.Status(status) : null, s.RequestId);
}

public sealed record DiscoverItemResource(
    string MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterPath,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? ReleaseDate,
    TitleStateResource Status)
{
    public static DiscoverItemResource From(DiscoverEntry e) => new(
        RequestApi.Type(e.MediaType),
        e.TmdbId,
        e.Title,
        e.Year,
        e.Overview,
        e.PosterPath,
        RequestApi.Image(e.PosterPath, "w342"),
        RequestApi.Image(e.BackdropPath, "w1280"),
        e.Rating,
        e.ReleaseDate,
        TitleStateResource.From(e.State));
}

public sealed record DiscoverPageResource(int Page, int TotalPages, IReadOnlyList<DiscoverItemResource> Results)
{
    public static DiscoverPageResource From(DiscoverResult r) => new(r.Page, r.TotalPages, [.. r.Results.Select(DiscoverItemResource.From)]);
}

public sealed record DiscoverCastResource(string Name, string? Character, string? ProfileUrl);

/// <param name="Status">Per-season state, the same shape as a title's.</param>
public sealed record DiscoverSeasonResource(
    int SeasonNumber,
    string? Name,
    int EpisodeCount,
    string? AirDate,
    string? PosterUrl,
    TitleStateResource Status);

/// <summary>Details of a title: the fields of a Discover result plus TMDB details, seasons and requests.</summary>
/// <param name="Requests">Requests of the title the caller may see (admins: all, others: their own).</param>
/// <param name="AutomationHold">Side-by-side mode is on: an approved request is added and monitored but not searched.</param>
public sealed record DiscoverDetailsResource(
    string MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterPath,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? ReleaseDate,
    TitleStateResource Status,
    string? Tagline,
    IReadOnlyList<string> Genres,
    int? Runtime,
    string? TmdbStatus,
    string? Certification,
    string? Network,
    IReadOnlyList<string> Studios,
    IReadOnlyList<DiscoverCastResource> Cast,
    IReadOnlyList<DiscoverSeasonResource> Seasons,
    IReadOnlyList<RequestResource> Requests,
    bool AutomationHold);
