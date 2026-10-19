// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Claims;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;

namespace StreamZH.Host.Users;

/// <summary>Who does something: the signed-in account, or the owner without account (id 0: API key, local network, External).</summary>
public readonly record struct UserActor(int Id, UserRole Role)
{
    public bool IsOwner => Role == UserRole.Owner;

    public static UserActor From(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new UserActor(UserClaims.UserId(principal), UserClaims.Role(principal) ?? UserRole.Requester);
    }
}

public enum UserError
{
    None,
    NotFound,
    Forbidden,
    Invalid,
    Conflict,
    Gone,
}

/// <summary>The outcome of a user management action: a value, or an error with a message or field errors.</summary>
public sealed record UserResult<T>(T? Value, UserError Error = UserError.None, string? Message = null, IDictionary<string, string[]>? Errors = null)
{
    public bool Ok => Error == UserError.None;

    public static UserResult<T> Success(T value) => new(value);

    public static UserResult<T> Fail(UserError error, string message) => new(default, error, message);

    public static UserResult<T> Invalid(IDictionary<string, string[]> errors) => new(default, UserError.Invalid, "Some fields are not valid.", errors);

    public UserResult<TOther> As<TOther>() => new(default, Error, Message, Errors);
}

/// <summary>A new or changed account. On update a null <see cref="Role"/> or <see cref="Enabled"/> keeps the current value.</summary>
public sealed record UserInput(
    string? Username = null,
    string? Password = null,
    string? Role = null,
    string? DisplayName = null,
    string? Email = null,
    bool? Enabled = null,
    bool? AutoApprove = null,
    int? MovieRequestLimit = null,
    int? SeriesRequestLimit = null,
    int? RequestLimitDays = null);

/// <summary>
/// User management (0.10) with its rules: an owner always remains (the last enabled owner can be neither lowered,
/// disabled nor deleted), only owners create, raise or change owners, nobody changes their own role or disables or
/// deletes themselves, and every change that should end sessions (disable, role change, password) bumps the session
/// version. Changes apply to running sessions at once (session and Basic caches are dropped).
/// </summary>
public sealed class UserService(
    UserRepository users,
    PasswordService passwords,
    RequestRepository requests,
    DiscordLinkRepository discordLinks,
    IDiscordLinkCodes linkCodes,
    UserSessionCache sessions,
    BasicCredentialCache basicCache,
    TimeProvider time,
    ILogger<UserService> logger)
{
    public const string LastOwnerMessage = "StreamZH needs at least one enabled owner.";

    // Owner checks and username uniqueness must not interleave.
    internal readonly Lock Sync = new();

    public IReadOnlyList<UserAccount> All() => users.All();

    public UserAccount? Find(int id) => users.FindAccount(id);

    public UserResult<UserAccount> Create(UserActor actor, UserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = UserRules.ValidateCredentials(input.Username, input.Password);
        var role = UserRules.ParseRole(input.Role);
        if (role is null)
        {
            errors["role"] = ["Choose owner, admin, friend or requester."];
        }
        else if (role == UserRole.Owner && !actor.IsOwner)
        {
            return UserResult<UserAccount>.Fail(UserError.Forbidden, "Only an owner can create owners.");
        }

        var account = Fields(new UserAccount { Role = role ?? UserRole.Friend, Enabled = input.Enabled ?? true }, input, errors);
        lock (Sync)
        {
            var username = input.Username?.Trim() ?? string.Empty;
            if (!errors.ContainsKey("username") && users.FindByUsername(username) is not null)
            {
                errors["username"] = ["This username is taken."];
            }

            if (errors.Count > 0)
            {
                return UserResult<UserAccount>.Invalid(errors);
            }

            var id = users.Create(account with { Username = username }, passwords.Hash(input.Password!));
            logger.LogInformation("User '{Username}' created as {Role} by user {Actor}", username, account.Role, actor.Id);
            return UserResult<UserAccount>.Success(users.FindAccount(id)!);
        }
    }

    public UserResult<UserAccount> Update(UserActor actor, int id, UserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (Sync)
        {
            var target = users.FindAccount(id);
            if (target is null)
            {
                return UserResult<UserAccount>.Fail(UserError.NotFound, "This user does not exist.");
            }

            if (target.Role == UserRole.Owner && !actor.IsOwner)
            {
                return UserResult<UserAccount>.Fail(UserError.Forbidden, "Only an owner can change an owner.");
            }

            var errors = new Dictionary<string, string[]>();
            var role = target.Role;
            if (input.Role is not null)
            {
                if (UserRules.ParseRole(input.Role) is { } parsed)
                {
                    role = parsed;
                }
                else
                {
                    errors["role"] = ["Choose owner, admin, friend or requester."];
                }
            }

            if (role == UserRole.Owner && target.Role != UserRole.Owner && !actor.IsOwner)
            {
                return UserResult<UserAccount>.Fail(UserError.Forbidden, "Only an owner can make someone an owner.");
            }

            var enabled = input.Enabled ?? target.Enabled;
            if (actor.Id == id && role != target.Role)
            {
                errors["role"] = ["You cannot change your own role."];
            }

            if (actor.Id == id && !enabled)
            {
                errors["enabled"] = ["You cannot disable your own account."];
            }

            if (target.Role == UserRole.Owner && target.Enabled && (role != UserRole.Owner || !enabled) && users.CountEnabledOwners() <= 1)
            {
                errors[role != UserRole.Owner ? "role" : "enabled"] = [LastOwnerMessage];
            }

            var updated = Fields(target with { Role = role, Enabled = enabled }, input, errors);
            if (errors.Count > 0)
            {
                return UserResult<UserAccount>.Invalid(errors);
            }

            users.Update(updated);
            if (role != target.Role || (target.Enabled && !enabled))
            {
                users.BumpSessionVersion(id);
            }

            Changed(id);
            logger.LogInformation("User '{Username}' changed by user {Actor}", target.Username, actor.Id);
            return UserResult<UserAccount>.Success(users.FindAccount(id)!);
        }
    }

    public UserResult<bool> Delete(UserActor actor, int id)
    {
        lock (Sync)
        {
            var target = users.FindAccount(id);
            if (target is null)
            {
                return UserResult<bool>.Fail(UserError.NotFound, "This user does not exist.");
            }

            if (target.Role == UserRole.Owner && !actor.IsOwner)
            {
                return UserResult<bool>.Fail(UserError.Forbidden, "Only an owner can delete an owner.");
            }

            if (actor.Id == id)
            {
                return UserResult<bool>.Fail(UserError.Conflict, "You cannot delete your own account.");
            }

            if (target.Role == UserRole.Owner && target.Enabled && users.CountEnabledOwners() <= 1)
            {
                return UserResult<bool>.Fail(UserError.Conflict, LastOwnerMessage);
            }

            // Requests stay (without the user); the Discord link and a pending link code go.
            requests.DetachUser(id);
            discordLinks.RemoveForUser(id);
            linkCodes.Cancel(id);
            users.Delete(id);
            Changed(id);
            logger.LogInformation("User '{Username}' deleted by user {Actor}", target.Username, actor.Id);
            return UserResult<bool>.Success(true);
        }
    }

    /// <summary>An admin sets a new password; every session of the user ends.</summary>
    public UserResult<User> ResetPassword(UserActor actor, int id, string? password)
    {
        var target = users.FindAccount(id);
        if (target is null)
        {
            return UserResult<User>.Fail(UserError.NotFound, "This user does not exist.");
        }

        if (target.Role == UserRole.Owner && !actor.IsOwner && actor.Id != id)
        {
            return UserResult<User>.Fail(UserError.Forbidden, "Only an owner can reset an owner's password.");
        }

        var errors = new Dictionary<string, string[]>();
        UserRules.AddPasswordError(errors, "password", password);
        if (errors.Count > 0)
        {
            return UserResult<User>.Invalid(errors);
        }

        users.ChangePassword(id, passwords.Hash(password!));
        Changed(id);
        logger.LogInformation("Password of '{Username}' reset by user {Actor}", target.Username, actor.Id);
        return UserResult<User>.Success(users.FindById(id)!);
    }

    /// <summary>Signs the user out everywhere.</summary>
    public UserResult<User> RevokeSessions(UserActor actor, int id)
    {
        var target = users.FindAccount(id);
        if (target is null)
        {
            return UserResult<User>.Fail(UserError.NotFound, "This user does not exist.");
        }

        if (target.Role == UserRole.Owner && !actor.IsOwner && actor.Id != id)
        {
            return UserResult<User>.Fail(UserError.Forbidden, "Only an owner can end an owner's sessions.");
        }

        users.BumpSessionVersion(id);
        Changed(id);
        logger.LogInformation("Sessions of '{Username}' ended by user {Actor}", target.Username, actor.Id);
        return UserResult<User>.Success(users.FindById(id)!);
    }

    public UserResult<bool> UnlinkDiscord(UserActor actor, int id)
    {
        var target = users.FindAccount(id);
        if (target is null)
        {
            return UserResult<bool>.Fail(UserError.NotFound, "This user does not exist.");
        }

        if (target.Role == UserRole.Owner && !actor.IsOwner && actor.Id != id)
        {
            return UserResult<bool>.Fail(UserError.Forbidden, "Only an owner can change an owner.");
        }

        discordLinks.RemoveForUser(id);
        linkCodes.Cancel(id);
        return UserResult<bool>.Success(true);
    }

    /// <summary>The user's own display name and e-mail address.</summary>
    public UserResult<UserAccount> UpdateProfile(int id, string? displayName, string? email)
    {
        if (users.FindAccount(id) is null)
        {
            return UserResult<UserAccount>.Fail(UserError.NotFound, "This session has no user account.");
        }

        var errors = new Dictionary<string, string[]>();
        var name = UserRules.DisplayName(displayName, errors);
        var mail = UserRules.Email(email, errors);
        if (errors.Count > 0)
        {
            return UserResult<UserAccount>.Invalid(errors);
        }

        users.UpdateProfile(id, name, mail);
        return UserResult<UserAccount>.Success(users.FindAccount(id)!);
    }

    /// <summary>The user changes their own password (current one required); other sessions end.</summary>
    public UserResult<User> ChangeOwnPassword(int id, string? currentPassword, string? newPassword)
    {
        var user = users.FindById(id);
        if (user is null)
        {
            return UserResult<User>.Fail(UserError.NotFound, "This session has no user account.");
        }

        var errors = new Dictionary<string, string[]>();
        if (passwords.Verify(user.Username, currentPassword ?? string.Empty)?.Id != id)
        {
            errors["currentPassword"] = ["The current password is wrong."];
        }

        UserRules.AddPasswordError(errors, "newPassword", newPassword);
        if (errors.Count > 0)
        {
            return UserResult<User>.Invalid(errors);
        }

        users.ChangePassword(id, passwords.Hash(newPassword!));
        Changed(id);
        logger.LogInformation("User '{Username}' changed their password", user.Username);
        return UserResult<User>.Success(users.FindById(id)!);
    }

    /// <summary>Creates an account for an accepted invite (called under <see cref="Sync"/> by the invite service).</summary>
    internal UserResult<User> CreateInvited(string? username, string? password, UserRole role, Func<bool> useInvite)
    {
        var errors = UserRules.ValidateCredentials(username, password);
        var name = username?.Trim() ?? string.Empty;
        if (!errors.ContainsKey("username") && users.FindByUsername(name) is not null)
        {
            errors["username"] = ["This username is taken."];
        }

        if (errors.Count > 0)
        {
            return UserResult<User>.Invalid(errors);
        }

        if (!useInvite())
        {
            return UserResult<User>.Fail(UserError.Gone, "This invite link is no longer valid.");
        }

        var id = users.Create(new UserAccount { Username = name, Role = role }, passwords.Hash(password!));
        users.SetLastLogin(id, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("User '{Username}' joined as {Role} with an invite", name, role);
        return UserResult<User>.Success(users.FindById(id)!);
    }

    /// <summary>Drops cached sessions so changes apply at once.</summary>
    private void Changed(int id)
    {
        sessions.Forget(id);
        basicCache.Clear();
    }

    /// <summary>Display name, e-mail, auto-approve and limits from the input, validated.</summary>
    private static UserAccount Fields(UserAccount account, UserInput input, Dictionary<string, string[]> errors)
    {
        var displayName = UserRules.DisplayName(input.DisplayName, errors);
        var email = UserRules.Email(input.Email, errors);
        var (movies, series, days) = UserRules.Limits(input.MovieRequestLimit, input.SeriesRequestLimit, input.RequestLimitDays, errors);
        return account with
        {
            DisplayName = displayName,
            Email = email,
            AutoApprove = input.AutoApprove,
            MovieRequestLimit = movies,
            SeriesRequestLimit = series,
            RequestLimitDays = days,
        };
    }
}
