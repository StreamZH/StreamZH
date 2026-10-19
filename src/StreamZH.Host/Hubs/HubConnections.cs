// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.General;
using StreamZH.Host.Security;

namespace StreamZH.Host.Hubs;

/// <summary>
/// The open live connections (/hub) and who opened them (0.12, security review S1-08). A WebSocket is authorized once,
/// when it opens; without this a user who is disabled, demoted, deleted or signed out would keep receiving events until
/// they close the tab. Account changes check that account's connections at once (<see cref="UserSessionCache.Changed"/>),
/// and every <see cref="Interval"/> all connections are checked against the current sign-in rules.
/// </summary>
public sealed class HubConnections(IServiceProvider services, ILogger<HubConnections> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _connections = new(StringComparer.Ordinal);

    public int Count => _connections.Count;

    public void Add(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User is not { } user)
        {
            return;
        }

        var apiKey = user.Identity?.AuthenticationType == AuthSchemes.ApiKey ? Fingerprint(services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ApiKey)) : null;
        _connections[context.ConnectionId] = new Entry(context, user, UserClaims.UserId(user), apiKey);
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Checks the connections of one account now; returns how many were closed.</summary>
    public int Revalidate(int userId) => Revalidate(e => e.UserId == userId);

    /// <summary>Checks every connection now; returns how many were closed.</summary>
    public int RevalidateAll() => Revalidate(_ => true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = services.GetRequiredService<UserSessionCache>();
        sessions.Changed += OnUserChanged;
        try
        {
            using var timer = new PeriodicTimer(Interval, services.GetRequiredService<TimeProvider>());
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RevalidateAll();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sessions.Changed -= OnUserChanged;
        }
    }

    private void OnUserChanged(int userId) => Revalidate(userId);

    private int Revalidate(Func<Entry, bool> which)
    {
        var closed = 0;
        foreach (var (id, entry) in _connections)
        {
            if (!which(entry))
            {
                continue;
            }

            bool valid;
            try
            {
                valid = IsStillValid(entry);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Checking live connection {Connection} failed", id);
                valid = false;
            }

            if (!valid && _connections.TryRemove(id, out _))
            {
                logger.LogInformation("Live connection of '{User}' closed: its sign-in is no longer valid", entry.User.Identity?.Name);
                entry.Context.Abort();
                closed++;
            }
        }

        return closed;
    }

    private bool IsStillValid(Entry entry)
    {
        var security = services.GetRequiredService<SecuritySettings>();
        return entry.User.Identity?.AuthenticationType switch
        {
            AuthSchemes.Cookie or AuthSchemes.Basic => services.GetRequiredService<UserSessionCache>().IsStillValid(entry.User, services.GetRequiredService<SessionStore>()),
            AuthSchemes.ApiKey => entry.ApiKey is not null && entry.ApiKey == Fingerprint(services.GetRequiredService<ConfigRepository>().Get(ConfigKeys.ApiKey)),
            AuthSchemes.Local => security.Method != AuthenticationMethod.External && security.Required == AuthenticationRequired.DisabledForLocalAddresses,
            AuthSchemes.External => security.Method == AuthenticationMethod.External,
            _ => false,
        };
    }

    private static string? Fingerprint(string? key) => key is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed record Entry(HubCallerContext Context, ClaimsPrincipal User, int UserId, string? ApiKey);
}
