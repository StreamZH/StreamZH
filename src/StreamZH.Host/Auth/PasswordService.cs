// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Identity;
using StreamZH.Data;

namespace StreamZH.Host.Auth;

/// <summary>PBKDF2 password hashing (ASP.NET Core Identity format, rehash on algorithm upgrades).</summary>
public sealed class PasswordService(UserRepository users)
{
    private readonly PasswordHasher<User> _hasher = new();

    // Verify against this when the user does not exist, so timing does not reveal valid usernames.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, "streamzh-dummy-password");

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public User? Verify(string username, string password)
    {
        var user = users.FindByUsername(username);
        var result = _hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, password);

        if (user is null || result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            users.UpdatePasswordHash(user.Id, Hash(password));
        }

        return user;
    }
}