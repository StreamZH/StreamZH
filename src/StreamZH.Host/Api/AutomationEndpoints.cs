// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Library.Search;

namespace StreamZH.Host.Api;

/// <summary>
/// Side-by-side mode (0.9): GET/PUT /api/v1/settings/automation { hold, since }. While "hold" is on StreamZH grabs nothing
/// automatically (see <see cref="AutomationHold"/>). Every signed-in user may read it, admins change it. The migration
/// wizard switches it on; nothing switches it off except the user.
/// </summary>
public static class AutomationEndpoints
{
    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/automation").WithTags("Settings");

        group.MapGet("/", (AutomationHold hold) => hold.State()).RequireAuthorization();

        group.MapPut("/", (UpdateAutomationHold request, AutomationHold hold) =>
        {
            if (request.Hold is not { } on)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hold"] = ["Use true (side-by-side, grab nothing automatically) or false."] });
            }

            return Results.Ok(hold.Set(on));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }
}

/// <summary>The body of PUT /api/v1/settings/automation; "since" is set by StreamZH.</summary>
public sealed record UpdateAutomationHold(bool? Hold);
