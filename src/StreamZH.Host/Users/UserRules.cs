// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mail;
using System.Text.RegularExpressions;
using StreamZH.Core.Users;

namespace StreamZH.Host.Users;

/// <summary>The input rules for accounts, shared by first-run setup, user management, invites and the profile.</summary>
public static partial class UserRules
{
    public const int MinPasswordLength = 8;

    // Hashing is slow on purpose; a cap keeps huge inputs from costing much.
    public const int MaxPasswordLength = 256;
    public const int MaxDisplayName = 100;
    public const int MaxEmail = 200;
    public const int MaxLimit = 10_000;
    public const int MaxLimitDays = 365;

    public const string UsernameMessage = "Use 3 to 50 characters: letters, numbers, dot, dash or underscore.";

    public static string PasswordMessage => $"Use at least {MinPasswordLength} characters.";

    public static bool IsValidUsername(string? username) => !string.IsNullOrWhiteSpace(username) && UsernameRegex().IsMatch(username.Trim());

    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length >= MinPasswordLength && password.Length <= MaxPasswordLength;

    /// <summary>Username and password errors keyed "username" and "password" (setup, create, invite).</summary>
    public static Dictionary<string, string[]> ValidateCredentials(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsValidUsername(username))
        {
            errors["username"] = [UsernameMessage];
        }

        AddPasswordError(errors, "password", password);
        return errors;
    }

    public static void AddPasswordError(Dictionary<string, string[]> errors, string field, string? password)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            errors[field] = [PasswordMessage];
        }
        else if (password.Length > MaxPasswordLength)
        {
            errors[field] = [$"Use at most {MaxPasswordLength} characters."];
        }
    }

    /// <summary>Trimmed display name, null when empty; an error when too long or with control characters.</summary>
    public static string? DisplayName(string? value, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (name.Length > MaxDisplayName || name.Any(char.IsControl))
        {
            errors["displayName"] = [$"Use at most {MaxDisplayName} characters."];
        }

        return name;
    }

    /// <summary>Trimmed e-mail address, null when empty (it is optional); an error when it is not an address.</summary>
    public static string? Email(string? value, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        if (email.Length > MaxEmail || !EmailRegex().IsMatch(email) || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            errors["email"] = ["Enter an e-mail address like jane@example.com, or leave it empty."];
        }

        return email;
    }

    public static UserRole? ParseRole(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "owner" => UserRole.Owner,
            "admin" => UserRole.Admin,
            "friend" => UserRole.Friend,
            "requester" => UserRole.Requester,
            _ => null,
        };

    public static string RoleName(UserRole role) => role switch
    {
        UserRole.Owner => "owner",
        UserRole.Admin => "admin",
        UserRole.Friend => "friend",
        _ => "requester",
    };

    /// <summary>Custom request limits: all or nothing. Without a period the role's defaults apply and the limits are dropped.</summary>
    public static (int? Movies, int? Series, int? Days) Limits(int? movies, int? series, int? days, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (days is null)
        {
            return (null, null, null);
        }

        if (days < 1 || days > MaxLimitDays)
        {
            errors["requestLimitDays"] = [$"Use 1 to {MaxLimitDays} days."];
        }

        if (movies is < 0 or > MaxLimit)
        {
            errors["movieRequestLimit"] = [$"Use 0 to {MaxLimit}, or leave it empty for no limit."];
        }

        if (series is < 0 or > MaxLimit)
        {
            errors["seriesRequestLimit"] = [$"Use 0 to {MaxLimit}, or leave it empty for no limit."];
        }

        return (movies, series, days);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$")]
    private static partial Regex UsernameRegex();

    // Something@something.tld without spaces; MailAddress does the rest.
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$")]
    private static partial Regex EmailRegex();
}
