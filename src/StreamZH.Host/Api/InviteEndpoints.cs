// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Requests;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.General;
using StreamZH.Host.Users;

namespace StreamZH.Host.Api;

/// <summary>
/// Invite links (0.10). Admins create, list and withdraw them; opening and accepting one needs no sign-in and is
/// rate-limited like the sign-in. The token is returned once, when the invite is created.
/// </summary>
public static class InviteEndpoints
{
    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/invites").WithTags("Users");

        group.MapGet("/", (InviteService invites, UserRepository users, SecuritySettings security, TimeProvider time) =>
        {
            var names = users.All().ToDictionary(u => u.Id, u => u.Name);
            var now = time.GetUtcNow().UtcDateTime;
            return new InviteList(
                [.. invites.All().Select(i => InviteResource.From(i, now, names.GetValueOrDefault(i.CreatedBy)))],
                security.Method == AuthenticationMethod.Forms);
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapPost("/", (InviteRequest request, HttpContext http, InviteService invites, HostSettings host, TimeProvider time) =>
        {
            var result = invites.Create(UserActor.From(http.User), new InviteInput(request.Role, request.Note, request.ExpiresInDays, request.MaxUses));
            if (!result.Ok)
            {
                return result.ToProblem();
            }

            var (invite, token) = result.Value!;
            var url = $"{BaseUrl(http, host)}/invite/{token}";
            return Results.Ok(new CreatedInviteResource(invite.Id, url, token, InviteResource.From(invite, time.GetUtcNow().UtcDateTime, null)));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Withdraws the invite; ?remove=true deletes it from the list.
        group.MapDelete("/{id:int}", (int id, bool? remove, InviteService invites) =>
            (remove == true ? invites.Delete(id) : invites.Revoke(id)) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/{token}", (string token, InviteService invites) =>
        {
            var (state, invite) = invites.Lookup(token);
            return state == InviteState.Valid
                ? new InviteCheck(true, null, UserRules.RoleName(invite!.Role), invite.Note, invite.Expires)
                : new InviteCheck(false, InviteService.Message(state), null, null, null);
        }).AllowAnonymous().RequireRateLimiting(AuthServiceExtensions.InviteRateLimit);

        group.MapPost("/{token}/accept", async (string token, AcceptInviteRequest request, HttpContext http, InviteService invites) =>
        {
            var result = invites.Accept(token, request.Username, request.Password);
            if (!result.Ok)
            {
                return result.ToProblem();
            }

            await AuthEndpoints.SignInAsync(http, result.Value!, persistent: true);
            return Results.Ok(UserInfo.From(result.Value!));
        }).AllowAnonymous().RequireRateLimiting(AuthServiceExtensions.InviteRateLimit);

        return app;
    }

    /// <summary>
    /// Settings → General → Host → Application URL when set (the address people use from outside, with the URL base),
    /// else the address this request came in on, with the URL base.
    /// </summary>
    internal static string BaseUrl(HttpContext http, HostSettings host) =>
        !string.IsNullOrWhiteSpace(host.ApplicationUrl)
            ? host.ApplicationUrl.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
}

public sealed record InviteRequest(string? Role, string? Note, int? ExpiresInDays, int? MaxUses);

public sealed record AcceptInviteRequest(string? Username, string? Password);

/// <param name="State">active, expired, usedUp or revoked.</param>
public sealed record InviteResource(
    int Id, string Role, string? Note, int CreatedBy, string? CreatedByName, DateTime Created, DateTime? Expires, int MaxUses, int Uses, bool Revoked, string State)
{
    public static InviteResource From(Invite invite, DateTime now, string? createdByName)
    {
        ArgumentNullException.ThrowIfNull(invite);
        var state = InviteService.StateOf(invite, now) switch
        {
            InviteState.Expired => "expired",
            InviteState.UsedUp => "usedUp",
            InviteState.Revoked => "revoked",
            _ => "active",
        };
        return new InviteResource(
            invite.Id, UserRules.RoleName(invite.Role), invite.Note, invite.CreatedBy, createdByName, invite.Created, invite.Expires, invite.MaxUses, invite.Uses, invite.Revoked, state);
    }
}

/// <param name="FormsSignIn">Invite links need the sign-in page (Settings → General → Security → Forms).</param>
public sealed record InviteList(IReadOnlyList<InviteResource> Records, bool FormsSignIn);

/// <summary>The link (with the token) is only in this answer.</summary>
public sealed record CreatedInviteResource(int Id, string Url, string Token, InviteResource Invite);

/// <summary>What the public invite page shows: whether the link works, for which role, and the note.</summary>
public sealed record InviteCheck(bool Valid, string? Message, string? Role, string? Note, DateTime? Expires);
