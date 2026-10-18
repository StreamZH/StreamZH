// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Host.General;

namespace StreamZH.Host.Api;

/// <summary>Settings → UI: date and time formats for the whole instance. Every signed-in user reads them, admins change them.</summary>
public static class UiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapUiSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/ui").WithTags("Settings");

        group.MapGet("/", (UiSettings ui) => UiSettingsResource.From(ui)).RequireAuthorization();

        group.MapPut("/", (UpdateUiSettings request, UiSettings ui) =>
        {
            var errors = new Dictionary<string, string[]>();
            Check(request.ShortDateFormat, UiSettings.ShortDateFormats, "shortDateFormat", errors);
            Check(request.LongDateFormat, UiSettings.LongDateFormats, "longDateFormat", errors);
            Check(request.TimeFormat, UiSettings.TimeFormats, "timeFormat", errors);
            Check(request.FirstDayOfWeek, UiSettings.FirstDaysOfWeek, "firstDayOfWeek", errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            ui.Update(request.ShortDateFormat, request.LongDateFormat, request.TimeFormat, request.ShowRelativeDates, request.FirstDayOfWeek);
            return Results.Ok(UiSettingsResource.From(ui));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        return app;
    }

    private static void Check(string? value, string[] allowed, string field, Dictionary<string, string[]> errors)
    {
        if (value is not null && !allowed.Contains(value))
        {
            errors[field] = [$"Use one of: {string.Join(", ", allowed)}."];
        }
    }
}

/// <summary>The formats with Radarr's tokens, or "locale" for the browser's own format.</summary>
public sealed record UiSettingsResource(
    string ShortDateFormat,
    string LongDateFormat,
    string TimeFormat,
    bool ShowRelativeDates,
    IReadOnlyList<string> ShortDateFormats,
    IReadOnlyList<string> LongDateFormats,
    IReadOnlyList<string> TimeFormats,
    string FirstDayOfWeek,
    IReadOnlyList<string> FirstDaysOfWeek)
{
    public static UiSettingsResource From(UiSettings ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return new UiSettingsResource(
            ui.ShortDateFormat, ui.LongDateFormat, ui.TimeFormat, ui.ShowRelativeDates, UiSettings.ShortDateFormats, UiSettings.LongDateFormats, UiSettings.TimeFormats,
            ui.FirstDayOfWeek, UiSettings.FirstDaysOfWeek);
    }
}

/// <summary>Null fields keep their value.</summary>
public sealed record UpdateUiSettings(
    string? ShortDateFormat = null, string? LongDateFormat = null, string? TimeFormat = null, bool? ShowRelativeDates = null, string? FirstDayOfWeek = null);
