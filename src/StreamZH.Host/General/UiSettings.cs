// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;

namespace StreamZH.Host.General;

/// <summary>
/// Settings → UI: how the web app shows dates and times, for every browser of this instance (like Radarr).
/// "locale" = the browser's own format, which is what StreamZH showed before these settings existed.
/// The patterns use Radarr's tokens (YYYY, MM, MMM, D, DD, dddd, HH, h, mm, a); the web app formats them.
/// </summary>
public sealed class UiSettings(SettingsCache cache)
{
    public const string Locale = "locale";

    public static readonly string[] ShortDateFormats = [Locale, "MMM D YYYY", "DD MMM YYYY", "MM/D/YYYY", "MM/DD/YYYY", "DD/MM/YYYY", "DD.MM.YYYY", "YYYY-MM-DD"];

    public static readonly string[] LongDateFormats = [Locale, "dddd, MMMM D YYYY", "dddd, D MMMM YYYY"];

    public static readonly string[] TimeFormats = [Locale, "HH:mm", "h:mm a"];

    public string ShortDateFormat => Pick(ConfigKeys.UiShortDateFormat, ShortDateFormats);

    public string LongDateFormat => Pick(ConfigKeys.UiLongDateFormat, LongDateFormats);

    public string TimeFormat => Pick(ConfigKeys.UiTimeFormat, TimeFormats);

    /// <summary>"3 min ago" instead of the date and time, for recent events.</summary>
    public bool ShowRelativeDates => cache.GetBool(ConfigKeys.UiShowRelativeDates, true);

    public void Update(string? shortDate, string? longDate, string? time, bool? relative)
    {
        if (shortDate is not null)
        {
            cache.Set(ConfigKeys.UiShortDateFormat, shortDate);
        }

        if (longDate is not null)
        {
            cache.Set(ConfigKeys.UiLongDateFormat, longDate);
        }

        if (time is not null)
        {
            cache.Set(ConfigKeys.UiTimeFormat, time);
        }

        if (relative is { } r)
        {
            cache.SetBool(ConfigKeys.UiShowRelativeDates, r);
        }
    }

    private string Pick(string key, string[] allowed) => cache.Get(key) is { } value && allowed.Contains(value) ? value : Locale;
}
