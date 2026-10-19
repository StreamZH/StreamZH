// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Tray;

namespace StreamZH.Tray.Core;

/// <summary>The texts of the tray icon: the status line in the menu and the tooltip.</summary>
public static class StatusText
{
    /// <summary>Windows cuts the notification area tooltip after 127 characters.</summary>
    public const int MaxTooltip = 127;

    private const string Separator = " · ";

    /// <summary>"Running · 2 downloads · 47.3 % · 12 min 05 s", "Paused", "Not running" …</summary>
    public static string Line(TrayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (state.Kind)
        {
            case TrayStateKind.NotRunning:
                return state.StartupError is { } error ? $"Not running · {error.Summary}" : "Not running";
            case TrayStateKind.Starting:
                return "Starting…";
            case TrayStateKind.Stopping:
                return "Stopping…";
            case TrayStateKind.NotResponding:
                return "Not responding";
            case TrayStateKind.NoAccess:
                return state.TokenProblem == TokenProblem.Missing
                    ? "Running · no status (tray.token missing, restart StreamZH)"
                    : "Running · no status (no access to tray.token)";
        }

        var status = state.Status!;
        var parts = new List<string> { state.Kind == TrayStateKind.Paused ? "Paused" : "Running" };
        if (status.Downloads > 0)
        {
            parts.Add(Count(status.Downloads, "download", "downloads"));
            parts.Add(Percent(status.Progress));
            if (state.Kind == TrayStateKind.Running && status.EtaSeconds is { } eta)
            {
                parts.Add(Duration(eta));
            }
        }
        else if (state.Kind == TrayStateKind.Running)
        {
            parts.Add("no downloads");
        }

        if (status.Errors > 0)
        {
            parts.Add(Count(status.Errors, "error", "errors"));
        }

        if (status.Warnings > 0)
        {
            parts.Add(Count(status.Warnings, "warning", "warnings"));
        }

        return string.Join(Separator, parts);
    }

    /// <summary>"StreamZH · " and the status line, at most 127 characters.</summary>
    public static string Tooltip(TrayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var name = state.Status?.InstanceName is { Length: > 0 } instance ? instance : "StreamZH";
        return Truncate(name + Separator + Line(state), MaxTooltip);
    }

    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
    }

    /// <summary>One decimal, rounded down, so 99.96 % shows 99.9 % and only a finished queue shows 100.0 %.</summary>
    public static string Percent(double fraction)
    {
        var tenths = double.IsFinite(fraction) ? Math.Floor(Math.Clamp(fraction, 0, 1) * 1000 + 1e-9) : 0;
        return (tenths / 10).ToString("0.0", CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>Time left, rounded up to whole seconds: "45 s", "12 min 05 s", "1 h 02 min", "2 d 03 h".</summary>
    public static string Duration(double seconds)
    {
        var total = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Ceiling(seconds - 1e-9) : 0;
        var span = TimeSpan.FromSeconds(total);
        var c = CultureInfo.InvariantCulture;
        if (total < 60)
        {
            return string.Create(c, $"{total} s");
        }

        if (total < 3600)
        {
            return string.Create(c, $"{span.Minutes} min {span.Seconds:00} s");
        }

        if (total < 86400)
        {
            return string.Create(c, $"{span.Hours} h {span.Minutes:00} min");
        }

        return string.Create(c, $"{(long)span.TotalDays} d {span.Hours:00} h");
    }

    private static string Count(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}

/// <summary>Which finished downloads get a notification: only those that finished while the tray was watching.</summary>
public sealed class NotificationTracker
{
    /// <summary>More finished at once than this: one summary instead of one notification each.</summary>
    public const int MaxSingle = 3;

    private string? _run;
    private long _last;

    /// <summary>The query for the next status call: the known run and the last number seen (null before the first answer).</summary>
    public (string? Run, long? After) Query => (_run, _run is null ? null : _last);

    /// <summary>
    /// The downloads to announce from this answer. The first answer only sets the starting point; after StreamZH restarted
    /// (another run) everything it reports is new.
    /// </summary>
    public IReadOnlyList<TrayFinishedDownload> Take(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_run is null)
        {
            _run = status.Run;
            _last = status.LastFinished;
            return [];
        }

        if (_run != status.Run)
        {
            _run = status.Run;
            _last = status.LastFinished;
            return [.. status.Finished.OrderBy(f => f.Number)];
        }

        var fresh = status.Finished.Where(f => f.Number > _last).OrderBy(f => f.Number).ToList();
        _last = Math.Max(_last, status.LastFinished);
        return fresh;
    }

    /// <summary>Balloon texts (title, text, is error) for <paramref name="finished"/>.</summary>
    public static IReadOnlyList<(string Title, string Text, bool Error)> Messages(IReadOnlyList<TrayFinishedDownload> finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        if (finished.Count <= MaxSingle)
        {
            return [.. finished.Select(f => f.Failed
                ? ("Download failed", StatusText.Truncate(f.Error is { Length: > 0 } error ? $"{f.Title}{Environment.NewLine}{error}" : f.Title, 255), true)
                : ("Download finished", StatusText.Truncate(f.Title, 255), false))];
        }

        var failed = finished.Count(f => f.Failed);
        var completed = finished.Count - failed;
        var text = failed == 0 ? $"{completed} downloads finished."
            : completed == 0 ? $"{failed} downloads failed."
            : $"{completed} downloads finished, {failed} failed.";
        return [(failed > 0 ? "Downloads finished, some failed" : "Downloads finished", text, failed > 0)];
    }
}
