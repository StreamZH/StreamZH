// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;

namespace StreamZH.Library.Wanted;

/// <summary>
/// The calendar as an iCalendar feed (RFC 5545) for Google Calendar, Outlook, Apple Calendar and others, like the iCal
/// feeds of Radarr and Sonarr. TMDB dates have no time of day, so every entry is an all-day event (VALUE=DATE), which
/// shows on the right day in every time zone.
/// </summary>
public static class ICalendarWriter
{
    public const string ContentType = "text/calendar; charset=utf-8";

    private const int MaxLineOctets = 75;

    public static string Write(IEnumerable<CalendarEntry> entries, DateTimeOffset now, string calendarName = "StreamZH")
    {
        ArgumentNullException.ThrowIfNull(entries);
        var output = new StringBuilder();
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        Line(output, "BEGIN:VCALENDAR");
        Line(output, "VERSION:2.0");
        Line(output, "PRODID:-//StreamZH//Calendar//EN");
        Line(output, "CALSCALE:GREGORIAN");
        Line(output, "METHOD:PUBLISH");
        Line(output, $"X-WR-CALNAME:{Escape(calendarName)}");
        Line(output, "X-PUBLISHED-TTL:PT1H");
        Line(output, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");

        foreach (var entry in entries)
        {
            var day = DateOnly.ParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Line(output, "BEGIN:VEVENT");
            Line(output, $"UID:streamzh-{entry.Key}@streamzh");
            Line(output, $"DTSTAMP:{stamp}");
            Line(output, $"DTSTART;VALUE=DATE:{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            Line(output, $"DTEND;VALUE=DATE:{day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            Line(output, $"SUMMARY:{Escape(Summary(entry))}");
            if (Description(entry) is { Length: > 0 } description)
            {
                Line(output, $"DESCRIPTION:{Escape(description)}");
            }

            Line(output, $"CATEGORIES:{(entry.Type == "movie" ? "Movies" : "Series")}");
            Line(output, $"STATUS:{(entry.Status == CalendarStatus.Unaired ? "TENTATIVE" : "CONFIRMED")}");
            Line(output, "TRANSP:TRANSPARENT");
            Line(output, "END:VEVENT");
        }

        Line(output, "END:VCALENDAR");
        return output.ToString();
    }

    /// <summary>"Dune: Part Two (In cinemas)" or "Breaking Bad - 1x03 - ...And the Bag's in the River" (Sonarr's style).</summary>
    public static string Summary(CalendarEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Type == "movie")
        {
            var what = entry.ReleaseType switch
            {
                CalendarService.Digital => "Digital release",
                CalendarService.Physical => "Physical release",
                _ => "In cinemas",
            };
            return $"{entry.Title} ({what})";
        }

        var number = string.Create(CultureInfo.InvariantCulture, $"{entry.SeasonNumber}x{entry.EpisodeNumber:00}");
        return string.IsNullOrWhiteSpace(entry.EpisodeTitle) ? $"{entry.Title} - {number}" : $"{entry.Title} - {number} - {entry.EpisodeTitle}";
    }

    private static string Description(CalendarEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Network))
        {
            parts.Add(entry.Network);
        }

        if (!string.IsNullOrWhiteSpace(entry.Overview))
        {
            parts.Add(entry.Overview.Trim());
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>TEXT escaping of RFC 5545 (3.3.11): backslash, semicolon, comma and line breaks.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\':
                    result.Append("\\\\");
                    break;
                case ';':
                    result.Append("\\;");
                    break;
                case ',':
                    result.Append("\\,");
                    break;
                case '\r':
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    result.Append("\\n");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                default:
                    // Other control characters are not allowed in TEXT values.
                    if (!char.IsControl(c) || c == '\t')
                    {
                        result.Append(c);
                    }

                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>Writes one content line, folded at 75 octets (UTF-8) with CRLF and a space, never inside a character.</summary>
    private static void Line(StringBuilder output, string line)
    {
        var octets = 0;
        var limit = MaxLineOctets;
        for (var i = 0; i < line.Length; i++)
        {
            var length = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(line.AsSpan(i, length));
            if (octets + size > limit)
            {
                output.Append("\r\n ");
                octets = 0;
                limit = MaxLineOctets - 1; // the leading space counts
            }

            output.Append(line, i, length);
            octets += size;
            i += length - 1;
        }

        output.Append("\r\n");
    }
}
