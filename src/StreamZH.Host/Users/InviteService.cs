// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Host.Users;

public enum InviteState
{
    Valid,
    Expired,
    UsedUp,
    Revoked,
    NotFound,
}

public sealed record InviteInput(string? Role, string? Note, int? ExpiresInDays, int? MaxUses);

/// <summary>A new invite with its token (shown once, only the SHA-256 is stored).</summary>
public sealed record CreatedInvite(Invite Invite, string Token);

/// <summary>
/// Invite links (0.10): an admin creates a link for a friend or requester; whoever opens it chooses a username and
/// password and is signed in. Tokens are 32 random bytes (base64url); only their SHA-256 is stored, so a copy of the
/// database does not reveal usable links.
/// </summary>
public sealed class InviteService(InviteRepository invites, UserService users, TimeProvider time, ILogger<InviteService> logger)
{
    public const int TokenBytes = 32;
    public const int TokenLength = 43;
    public const int MaxNote = 200;
    public const int MaxUses = 100;
    public const int MaxDays = 365;

    public IReadOnlyList<Invite> All() => invites.All();

    public UserResult<CreatedInvite> Create(UserActor actor, InviteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new Dictionary<string, string[]>();
        var role = UserRules.ParseRole(input.Role);
        if (role is not (UserRole.Friend or UserRole.Requester))
        {
            errors["role"] = ["Invites are for friends or requesters."];
        }

        var note = input.Note?.Trim();
        if (note?.Length > MaxNote || note?.Any(char.IsControl) == true)
        {
            errors["note"] = [$"Use at most {MaxNote} characters."];
        }

        if (input.ExpiresInDays is < 1 or > MaxDays)
        {
            errors["expiresInDays"] = [$"Use 1 to {MaxDays} days, or no expiry."];
        }

        var maxUses = input.MaxUses ?? 1;
        if (maxUses is < 1 or > MaxUses)
        {
            errors["maxUses"] = [$"Use 1 to {MaxUses}."];
        }

        if (errors.Count > 0)
        {
            return UserResult<CreatedInvite>.Invalid(errors);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var expires = input.ExpiresInDays is { } days ? time.GetUtcNow().UtcDateTime.AddDays(days) : (DateTime?)null;
        var id = invites.Add(Hash(token), role!.Value, string.IsNullOrEmpty(note) ? null : note, actor.Id, expires, maxUses);
        logger.LogInformation("Invite {Id} for a {Role} created by user {Actor}", id, role, actor.Id);
        return UserResult<CreatedInvite>.Success(new CreatedInvite(invites.Find(id)!, token));
    }

    public bool Revoke(int id)
    {
        if (invites.Find(id) is null)
        {
            return false;
        }

        invites.Revoke(id);
        return true;
    }

    public bool Delete(int id)
    {
        if (invites.Find(id) is null)
        {
            return false;
        }

        invites.Delete(id);
        return true;
    }

    public (InviteState State, Invite? Invite) Lookup(string? token)
    {
        if (!LooksLikeToken(token) || invites.FindByHash(Hash(token!)) is not { } invite)
        {
            return (InviteState.NotFound, null);
        }

        return (StateOf(invite, time.GetUtcNow().UtcDateTime), invite);
    }

    public static InviteState StateOf(Invite invite, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(invite);
        return invite.Revoked ? InviteState.Revoked
            : invite.Uses >= invite.MaxUses ? InviteState.UsedUp
            : invite.Expires is { } expires && expires <= now ? InviteState.Expired
            : InviteState.Valid;
    }

    /// <summary>Creates the account with the invite's role; the invite counts one use only when the account is created.</summary>
    public UserResult<User> Accept(string? token, string? username, string? password)
    {
        lock (users.Sync)
        {
            var (state, invite) = Lookup(token);
            if (state == InviteState.NotFound)
            {
                return UserResult<User>.Fail(UserError.NotFound, "This invite link does not exist.");
            }

            if (state != InviteState.Valid)
            {
                return UserResult<User>.Fail(UserError.Gone, Message(state));
            }

            return users.CreateInvited(username, password, invite!.Role, () => invites.TryUse(invite.Id, time.GetUtcNow().UtcDateTime));
        }
    }

    public static string Message(InviteState state) => state switch
    {
        InviteState.Expired => "This invite link has expired. Ask for a new one.",
        InviteState.UsedUp => "This invite link has already been used. Ask for a new one.",
        InviteState.Revoked => "This invite link was withdrawn. Ask for a new one.",
        InviteState.NotFound => "This invite link does not exist.",
        _ => string.Empty,
    };

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool LooksLikeToken(string? token) =>
        token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
