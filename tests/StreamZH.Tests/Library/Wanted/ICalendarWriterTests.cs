// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using StreamZH.Library.Wanted;

namespace StreamZH.Tests.Library.Wanted;

public sealed class ICalendarWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 30, 5, TimeSpan.FromHours(2));

    private static CalendarEntry Movie(string title, string date, string type = CalendarService.InCinemas, string status = CalendarStatus.Missing, string? overview = null) =>
        new($"movie-1-{type}", "movie", type, date, 1, null, null, title, 2026, null, null, null, overview, null, true, false, status, null, null);

    private static CalendarEntry Episode(string series, int season, int episode, string? title, string date, string? overview = null, string? network = null) =>
        new($"episode-{season * 100 + episode}", "episode", CalendarService.AirDate, date, null, 7, season * 100 + episode, series, 2008, season, episode, title,
            overview, network, true, false, CalendarStatus.Unaired, null, null);

    /// <summary>RFC 5545 3.1: remove every CRLF followed by a space.</summary>
    private static string[] Unfold(string text) => text.Replace("\r\n ", string.Empty, StringComparison.Ordinal).Split("\r\n");

    [Fact]
    public void Writes_a_valid_calendar_with_all_day_events()
    {
        var text = ICalendarWriter.Write(
            [Movie("Dune: Part Three", "2026-12-31", CalendarService.Digital), Episode("Breaking Bad", 1, 3, "...And the Bag's in the River", "2026-10-18", network: "AMC")],
            Now);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//StreamZH//Calendar//EN\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var lines = Unfold(text);
        Assert.Equal(2, lines.Count(l => l == "BEGIN:VEVENT"));
        Assert.Equal(2, lines.Count(l => l == "END:VEVENT"));
        Assert.Contains("UID:streamzh-movie-1-digital@streamzh", lines);
        Assert.Contains("DTSTAMP:20261018T073005Z", lines);

        // All-day events; the end is the next day (exclusive), also across the year.
        Assert.Contains("DTSTART;VALUE=DATE:20261231", lines);
        Assert.Contains("DTEND;VALUE=DATE:20270101", lines);
        Assert.Contains("DTSTART;VALUE=DATE:20261018", lines);
        Assert.Contains("DTEND;VALUE=DATE:20261019", lines);

        Assert.Contains("SUMMARY:Dune: Part Three (Digital release)", lines);
        Assert.Contains("SUMMARY:Breaking Bad - 1x03 - ...And the Bag's in the River", lines);
        Assert.Contains("DESCRIPTION:AMC", lines);
        Assert.Contains("STATUS:TENTATIVE", lines);
        Assert.Contains("STATUS:CONFIRMED", lines);
        Assert.Contains("CATEGORIES:Series", lines);
    }

    [Fact]
    public void Text_values_are_escaped()
    {
        Assert.Equal(@"Love\, Death \; Robots \\ 2\nNext line\nThird", ICalendarWriter.Escape("Love, Death ; Robots \\ 2\r\nNext line\nThird"));
        Assert.Equal("tab\tkept, bell gone".Replace(",", "\\,", StringComparison.Ordinal), ICalendarWriter.Escape("tab\tkept, bell\a gone"));

        var lines = Unfold(ICalendarWriter.Write([Movie("Love, Death; Robots", "2026-10-18", overview: "Line one.\nLine two; with, commas")], Now));
        Assert.Contains(@"SUMMARY:Love\, Death\; Robots (In cinemas)", lines);
        Assert.Contains(@"DESCRIPTION:Line one.\nLine two\; with\, commas", lines);
    }

    [Fact]
    public void Long_lines_are_folded_at_75_octets_without_splitting_characters()
    {
        var overview = string.Concat(Enumerable.Repeat("Über die Brücke 🎬 läuft ein Zürcher Film. ", 12));
        var text = ICalendarWriter.Write([Movie("Züri brännt", "2026-10-18", overview: overview)], Now);

        var physical = text.Split("\r\n");
        Assert.All(physical, line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"{Encoding.UTF8.GetByteCount(line)} octets: {line}"));
        Assert.Contains(physical, line => line.StartsWith(' '));
        Assert.All(physical, line => Assert.False(line.Length > 0 && char.IsHighSurrogate(line[^1]), "an emoji was split"));

        // Unfolding gives back the whole value.
        Assert.Contains($"DESCRIPTION:{overview.Trim()}", Unfold(text));
    }

    [Fact]
    public void An_empty_calendar_is_still_valid()
    {
        var text = ICalendarWriter.Write([], Now);
        Assert.DoesNotContain("VEVENT", text, StringComparison.Ordinal);
        Assert.Contains("X-WR-CALNAME:StreamZH\r\n", text, StringComparison.Ordinal);
    }
}
