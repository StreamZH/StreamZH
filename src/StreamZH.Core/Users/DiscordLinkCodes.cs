// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace StreamZH.Core.Users;

/// <summary>A one-time code the user types as <c>/link CODE</c> in Discord to link their Discord account.</summary>
public sealed record DiscordLinkCode(string Code, DateTimeOffset Expires);

/// <summary>
/// One-time codes that link a Discord account to a StreamZH user (0.10). The web profile creates a code, the Discord
/// bot redeems it and then stores the link (DiscordLinkRepository.Set).
/// </summary>
public interface IDiscordLinkCodes
{
    /// <summary>A new code for the user; an older code of the same user stops working.</summary>
    DiscordLinkCode Create(int userId);

    /// <summary>Redeems a code once: true with the user id when it exists and has not expired. Case and spaces do not matter.</summary>
    bool TryRedeem(string code, out int userId);

    /// <summary>The user's code that is still valid, if any.</summary>
    DiscordLinkCode? Active(int userId);

    /// <summary>Drops the user's code (account deleted, link removed).</summary>
    void Cancel(int userId);
}

/// <summary>In-memory link codes: 6 characters from an alphabet without look-alikes, valid for <see cref="Lifetime"/>, one per user.</summary>
public sealed class DiscordLinkCodes(TimeProvider time) : IDiscordLinkCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int Length = 6;

    // No 0/O, 1/I/L: easy to read and to type.
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, (int UserId, DateTimeOffset Expires)> _codes = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public DiscordLinkCode Create(int userId)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Prune(now);
            Cancel(userId);

            string code;
            do
            {
                code = RandomNumberGenerator.GetString(Alphabet, Length);
            }
            while (_codes.ContainsKey(code));

            var expires = now + Lifetime;
            _codes[code] = (userId, expires);
            return new DiscordLinkCode(code, expires);
        }
    }

    public bool TryRedeem(string code, out int userId)
    {
        userId = 0;
        var normalized = Normalize(code);
        if (normalized.Length != Length)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_codes.TryRemove(normalized, out var entry) || entry.Expires <= time.GetUtcNow())
            {
                return false;
            }

            userId = entry.UserId;
            return true;
        }
    }

    public DiscordLinkCode? Active(int userId)
    {
        var now = time.GetUtcNow();
        foreach (var (code, entry) in _codes)
        {
            if (entry.UserId == userId && entry.Expires > now)
            {
                return new DiscordLinkCode(code, entry.Expires);
            }
        }

        return null;
    }

    public void Cancel(int userId)
    {
        foreach (var (code, entry) in _codes)
        {
            if (entry.UserId == userId)
            {
                _codes.TryRemove(code, out _);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (code, entry) in _codes)
        {
            if (entry.Expires <= now)
            {
                _codes.TryRemove(code, out _);
            }
        }
    }

    private static string Normalize(string? code) =>
        new([.. (code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant)]);
}
