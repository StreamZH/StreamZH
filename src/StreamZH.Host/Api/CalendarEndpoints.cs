// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Library.Wanted;

namespace StreamZH.Host.Api;

/// <summary>Calendar (movie releases and episode air dates) and its iCal feed, like Radarr's and Sonarr's.</summary>
public static class CalendarEndpoints
{
    public const int DefaultPastDays = 7;
    public const int DefaultFutureDays = 28;
    public const int MaxPastDays = 365;
    public const int MaxFutureDays = 365;

    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var calendar = app.MapGroup("/api/v1/calendar").WithTags("Calendar").RequireAuthorization();

        // start and end are days (yyyy-MM-dd, inclusive). A date and time with an offset counts as the day in that offset,
        // so "2026-10-01T00:00:00+02:00" is October 1st, not September 30th in UTC.
        calendar.MapGet("/", (string? start, string? end, bool? unmonitored, string? type, CalendarService service, TimeProvider time) =>
        {
            var errors = new Dictionary<string, string[]>();
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var from = Day(start, today, "start", errors);
            var to = Day(end, from.AddDays(6), "end", errors);
            var (movies, series) = Types(type, "type", errors);
            if (errors.Count == 0 && to < from)
            {
                errors["end"] = ["The end must not be before the start."];
            }
            else if (errors.Count == 0 && to.DayNumber - from.DayNumber + 1 > CalendarQuery.MaxDays)
            {
                errors["end"] = [$"Ask for at most {CalendarQuery.MaxDays} days at once."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return Results.Ok(service.Entries(new CalendarQuery(from, to, unmonitored ?? false, movies, series)));
        });

        // The iCal feed for calendar apps: ?apikey=… signs it in like any API call (calendar apps cannot send a header).
        calendar.MapGet("/feed.ics", (int? pastDays, int? futureDays, bool? unmonitored, string? tags, CalendarService service, TimeProvider time) =>
        {
            var errors = new Dictionary<string, string[]>();
            var past = pastDays ?? DefaultPastDays;
            var future = futureDays ?? DefaultFutureDays;
            if (past is < 0 or > MaxPastDays)
            {
                errors["pastDays"] = [$"Use 0 to {MaxPastDays} days."];
            }

            if (future is < 0 or > MaxFutureDays)
            {
                errors["futureDays"] = [$"Use 0 to {MaxFutureDays} days."];
            }

            var (movies, series) = Types(tags, "tags", errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var now = time.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var entries = service.Entries(new CalendarQuery(today.AddDays(-past), today.AddDays(future), unmonitored ?? false, movies, series));
            return Results.Text(ICalendarWriter.Write(entries, now), ICalendarWriter.ContentType);
        });

        return app;
    }

    private static DateOnly Day(string? value, DateOnly fallback, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            return DateOnly.FromDateTime(moment.DateTime);
        }

        errors[field] = ["Use a date like 2026-10-18."];
        return fallback;
    }

    /// <summary>"movies", "series" or both (comma-separated); empty = both.</summary>
    private static (bool Movies, bool Series) Types(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        var movies = false;
        var series = false;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "movies" or "movie":
                    movies = true;
                    break;
                case "series" or "episodes":
                    series = true;
                    break;
                default:
                    errors[field] = ["Use movies, series or both (movies,series)."];
                    return (true, true);
            }
        }

        return (movies, series);
    }
}
