// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Library.Wanted;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Wanted;

/// <summary>Calendar entries (fake clock: Saturday 2026-10-17 12:00 UTC).</summary>
public sealed class CalendarServiceTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    public CalendarServiceTests()
    {
        _bed.Dune.InCinemas = "2026-10-10";
        _bed.Dune.DigitalRelease = "2026-10-24";
        _bed.Dune.PhysicalRelease = "2026-11-15";
        _bed.Movies.Update(_bed.Dune);
        _bed.Series.UpsertEpisodes(
            _bed.Show.Id,
            [
                .. _bed.Episodes,
                new Episode { SeasonNumber = 6, EpisodeNumber = 1, Title = "Today", AirDate = "2026-10-17" },
                new Episode { SeasonNumber = 6, EpisodeNumber = 2, Title = "Halloween", AirDate = "2026-10-31" },
                new Episode { SeasonNumber = 6, EpisodeNumber = 3, Title = "November", AirDate = "2026-11-01" },
            ],
            monitorNew: true);
    }

    private static CalendarQuery October(bool unmonitored = false, bool movies = true, bool series = true) =>
        new(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 31), unmonitored, movies, series);

    [Fact]
    public void Entries_cover_the_range_inclusive_and_come_in_date_order()
    {
        var entries = _bed.Calendar().Entries(October());

        Assert.Equal(
            [("2026-10-10", CalendarService.InCinemas), ("2026-10-17", CalendarService.AirDate), ("2026-10-24", CalendarService.Digital), ("2026-10-31", CalendarService.AirDate)],
            entries.Select(e => (e.Date, e.ReleaseType)));
        Assert.Equal(["movie-" + _bed.Dune.Id + "-inCinemas", "episode-" + _bed.Episode(6, 1).Id], entries.Take(2).Select(e => e.Key));
        Assert.Equal(("Breaking Bad", 6, 1, "Today"), (entries[1].Title, entries[1].SeasonNumber, entries[1].EpisodeNumber, entries[1].EpisodeTitle));

        // One day; a range that ends before it starts is empty.
        Assert.Single(_bed.Calendar().Entries(new CalendarQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1))));
        Assert.Empty(_bed.Calendar().Entries(new CalendarQuery(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1))));
    }

    [Fact]
    public void Status_is_downloaded_downloading_missing_or_unaired()
    {
        var entries = _bed.Calendar().Entries(October());
        Assert.Equal(CalendarStatus.Missing, entries.Single(e => e.EpisodeTitle == "Today").Status);
        Assert.Equal(CalendarStatus.Unaired, entries.Single(e => e.EpisodeTitle == "Halloween").Status);

        // In cinemas since October 10th: with the default minimum availability the movie is missing on every date.
        Assert.All(entries.Where(e => e.Type == "movie"), e => Assert.Equal(CalendarStatus.Missing, e.Status));
        _bed.Options.Save(_bed.Options.Values with { MinimumAvailability = MinimumAvailability.Released });
        Assert.All(_bed.Calendar().Entries(October()).Where(e => e.Type == "movie"), e => Assert.Equal(CalendarStatus.Unaired, e.Status));

        _bed.Series.SetEpisodeFiles(_bed.Show.Id, new Dictionary<(int, int), MediaFile> { [(6, 1)] = new("S06E01.mkv", 1, DateTime.UtcNow) });
        _bed.History.Add(new HistoryItem { Date = _bed.Time.Now.UtcDateTime, EventType = HistoryEventType.Grabbed, MediaType = MediaType.Movie, MovieId = _bed.Dune.Id, SourceTitle = "Dune" });

        entries = _bed.Calendar().Entries(October());
        var today = entries.Single(e => e.EpisodeTitle == "Today");
        Assert.Equal((CalendarStatus.Downloaded, true), (today.Status, today.HasFile));
        Assert.All(entries.Where(e => e.Type == "movie"), e => Assert.Equal((CalendarStatus.Downloading, QueueSnapshot.Grabbed), (e.Status, e.Queue)));
    }

    [Fact]
    public void Unmonitored_items_and_types_can_be_filtered()
    {
        _bed.Series.SetEpisodeMonitored(_bed.Episode(6, 2).Id, false);
        _bed.Dune.Monitored = false;
        _bed.Movies.Update(_bed.Dune);

        Assert.Equal(["Today"], _bed.Calendar().Entries(October()).Select(e => e.EpisodeTitle));
        var all = _bed.Calendar().Entries(October(unmonitored: true));
        Assert.Equal(4, all.Count);
        Assert.False(all.Single(e => e.EpisodeTitle == "Halloween").Monitored);

        Assert.All(_bed.Calendar().Entries(October(unmonitored: true, series: false)), e => Assert.Equal("movie", e.Type));
        Assert.All(_bed.Calendar().Entries(October(unmonitored: true, movies: false)), e => Assert.Equal("episode", e.Type));

        // An unmonitored series hides all of its episodes.
        _bed.Show.Monitored = false;
        _bed.Series.Update(_bed.Show);
        Assert.Empty(_bed.Calendar().Entries(October()));
    }

    public void Dispose() => _bed.Dispose();
}
