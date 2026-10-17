// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Host.Logging;

namespace StreamZH.Host.Api;

/// <summary>System → Logs: the newest entries (filtered by level and text) and the log files (download, delete).</summary>
public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system/logs").WithTags("System").RequireAuthorization(AuthSchemes.AdminPolicy);

        // level: minimum file level (Trace, Debug, Info, Warn, Error, Fatal); search: text in message, logger or details.
        group.MapGet("/", (FileLoggerProvider files, int? lines, string? level, string? search) =>
        {
            var entries = files.Entries(Math.Clamp(lines ?? 200, 1, 5000), level, search);
            return new LogTail(
                [.. entries.Reverse().SelectMany(Lines)],
                entries);
        });

        group.MapGet("/files", (FileLoggerProvider files) => files.Files());

        group.MapGet("/files/{name}", (string name, FileLoggerProvider files) =>
        {
            if (files.PathOf(name) is not { } path)
            {
                return Results.NotFound();
            }

            // Shared read: the current file stays open for writing.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Results.File(stream, "text/plain; charset=utf-8", name);
        });

        group.MapDelete("/files/{name}", (string name, FileLoggerProvider files) => files.Delete(name) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    private static IEnumerable<string> Lines(LogEntry entry)
    {
        yield return $"{entry.Time}|{entry.Level}|{entry.Logger}|{entry.Message}";
        if (entry.Details is { } details)
        {
            foreach (var line in details.Split('\n'))
            {
                yield return line.TrimEnd('\r');
            }
        }
    }
}

/// <summary><see cref="Lines"/>: the raw lines, oldest first; <see cref="Entries"/>: the same entries parsed, newest first.</summary>
public sealed record LogTail(IReadOnlyList<string> Lines, IReadOnlyList<LogEntry> Entries);
