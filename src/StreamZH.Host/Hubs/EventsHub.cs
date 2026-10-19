// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using StreamZH.Core.Users;

namespace StreamZH.Host.Hubs;

/// <summary>
/// Pushes events to the web UI. Every signed-in user may connect (0.10); owners, admins and friends join
/// <see cref="Members"/> and get every event, each user also joins their own group, through which requesters get only
/// "requestChanged" of their own requests.
/// </summary>
public sealed class EventsHub : Hub
{
    public const string Members = "members";

    public static string UserGroup(int userId) => $"user:{userId.ToString(CultureInfo.InvariantCulture)}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user is not null && (user.IsInRole(nameof(UserRole.Owner)) || user.IsInRole(nameof(UserRole.Admin)) || user.IsInRole(nameof(UserRole.Friend))))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Members);
        }

        if (int.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(id));
        }

        await base.OnConnectedAsync();
    }
}
