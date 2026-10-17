// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;

namespace StreamZH.Core.Decisions;

/// <summary>Size helpers: quality definitions are in MB per minute, releases in bytes.</summary>
public static class SizeRules
{
    public const long Megabyte = 1024L * 1024L;

    /// <summary>
    /// Total runtime the release should cover: the movie runtime, or episode runtime × episode count for series.
    /// 0 when the runtime is unknown.
    /// </summary>
    public static int TotalRuntimeMinutes(DecisionTarget target)
    {
        if (target.RuntimeMinutes <= 0)
        {
            return 0;
        }

        return target.MediaType == MediaType.Series
            ? target.RuntimeMinutes * Math.Max(1, target.EpisodeCount)
            : target.RuntimeMinutes;
    }

    /// <summary>Megabytes per minute × minutes, in bytes.</summary>
    public static long ToBytes(double megabytesPerMinute, int minutes) =>
        (long)Math.Round(megabytesPerMinute * Megabyte * minutes);

    /// <summary>Human-readable size, e.g. "1.5 GB" (binary units, like the *arr apps).</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    /// <summary>Describes the runtime the limits were computed for, e.g. "120 min" or "10 episodes, 450 min".</summary>
    public static string DescribeRuntime(DecisionTarget target, int totalMinutes) =>
        target.MediaType == MediaType.Series && target.EpisodeCount > 1
            ? $"{target.EpisodeCount} episodes, {totalMinutes} min"
            : $"{totalMinutes} min";
}
