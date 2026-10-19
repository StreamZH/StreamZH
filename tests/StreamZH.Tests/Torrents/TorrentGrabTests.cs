// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Library.Indexers;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Torrents;

/// <summary>
/// Grab routing (0.11): torrent releases go through the indexer client (which answers with the .torrent or a magnet link
/// as text) to the torrent client; Usenet releases keep their path.
/// </summary>
public sealed class TorrentGrabTests : IDisposable
{
    private readonly TrackerBed _bed = new();

    public TorrentGrabTests()
    {
        // The fake indexer answers like the Torznab client: /magnet/… → the magnet as text, /html/… → an error page, else the .torrent.
        _bed.Bed.Client.Nzb = (_, release) =>
        {
            var name = release.DownloadUrl.Split('/')[^1];
            return release.DownloadUrl.Contains("/magnet/", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(TorrentFixtures.Magnet(name))
                : release.DownloadUrl.Contains("/html/", StringComparison.Ordinal) ? "<html>Download limit reached</html>"u8.ToArray()
                : TorrentFixtures.Create(name);
        };
    }

    private static IndexerDefinition Torznab(int id = 7) =>
        FakeIndexerProvider.Definition(id, "Tracker") with
        {
            Implementation = IndexerDefinition.Torznab,
            BaseUrl = "https://tracker.example.org",
            ApiKey = "torznab-key-123",
            SeedRatio = 1.5,
            SeedTimeMinutes = 1440,
            SeasonPackSeedTimeMinutes = 4320,
        };

    private static ReleaseInfo Release(string title, string kind = "dl") => new()
    {
        Guid = $"guid-{title}",
        Title = title,
        DownloadUrl = $"https://tracker.example.org/{kind}/{title}",
        Size = 4_000_000_000,
        PublishDate = DateTimeOffset.UtcNow.AddDays(-1),
        IndexerId = 7,
        Indexer = "Tracker",
        Protocol = DownloadProtocol.Torrent,
        Seeders = 42,
        Peers = 50,
    };

    private static ReleaseDecision Decision(ReleaseInfo release, MediaType type = MediaType.Movie, IReadOnlyList<int>? episodes = null)
    {
        var parsed = ReleaseParser.Parse(release.Title)!;
        return new ReleaseDecision(
            release,
            parsed,
            new Decision(new ReleaseCandidate(parsed, release.Size, release.Indexer, 25, release.PublishDate, release.DownloadUrl, release.Guid), []),
            new SearchSubject(type, type == MediaType.Movie ? 1 : null, type == MediaType.Series ? 1 : null, "Subject"),
            episodes ?? []);
    }

    private GrabService Grab(IIndexerClient? client = null) => new(
        _bed.Bed.Indexers, client ?? _bed.Bed.Client, _bed.Bed.Cache, _bed.Bed.Clients, _bed.Bed.Downloads, _bed.Bed.History, _bed.Bed.Events,
        NullLogger<GrabService>.Instance, _bed.Bed.Time, torrents: _bed.Tracker);

    [Fact]
    public async Task A_torrent_file_goes_to_the_torrent_client_with_the_indexer_seed_goals()
    {
        _bed.Bed.Indexers.Indexers.Add(Torznab());
        var result = await Grab().GrabAsync(Decision(Release("Generated.Movie.2021.1080p.BluRay.x264-GRP")));

        Assert.Single(_bed.Bed.Client.Downloads);
        var added = Assert.Single(_bed.Client.Added);
        Assert.NotNull(added.File);
        Assert.Null(added.MagnetUri);
        Assert.Equal((1.5, 1440), (added.SeedRatio!.Value, added.SeedTimeMinutes!.Value));
        Assert.Equal(TorrentMetadata.InfoHash(added.File!), result.NzbPath);
        Assert.Empty(_bed.Bed.NzbFiles());
        Assert.Contains(7, _bed.Bed.Indexers.Successes);

        var row = Assert.Single(_bed.Downloads.TrackedTorrents());
        Assert.Equal((1.5, 1440, "Tracker", MediaType.Movie), (row.SeedRatio!.Value, row.SeedTimeMinutes!.Value, row.Indexer, row.MediaType!.Value));
        var history = _bed.Bed.History.Page(1, 10).Records.Single(h => h.EventType == HistoryEventType.Grabbed);
        Assert.Equal(("torrent", "qBittorrent", "file", "42"), (history.Data["protocol"], history.Data["downloadClient"], history.Data["torrentSource"], history.Data["seeders"]));
        Assert.Equal(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), history.Data["downloadId"]);
        Assert.Single(_bed.Bed.Grabbed);
    }

    [Fact]
    public async Task A_magnet_answer_is_handed_on_as_magnet()
    {
        _bed.Bed.Indexers.Indexers.Add(Torznab());
        await Grab().GrabAsync(Decision(Release("Generated.Redirect.2020.720p", "magnet")));
        var added = Assert.Single(_bed.Client.Added);
        Assert.Null(added.File);
        Assert.Equal(TorrentFixtures.Magnet("Generated.Redirect.2020.720p"), added.MagnetUri);
        Assert.Equal("magnet", _bed.Bed.History.Page(1, 10).Records.Single(h => h.EventType == HistoryEventType.Grabbed).Data["torrentSource"]);
    }

    [Fact]
    public async Task Season_packs_seed_longer_and_use_the_series_category()
    {
        _bed.Bed.Indexers.Indexers.Add(Torznab());
        var episodes = _bed.Bed.Episodes.Where(e => e.SeasonNumber == 1).Select(e => e.Id).ToList();
        await Grab().GrabAsync(Decision(Release("Generated.Show.S01.1080p.WEB-DL.x264-GRP"), MediaType.Series, episodes));
        Assert.Equal(4320, _bed.Client.Added[0].SeedTimeMinutes);
        Assert.Equal("streamzh-tv", _bed.Client.Added[0].Category);
    }

    [Fact]
    public async Task An_error_page_is_an_indexer_error()
    {
        _bed.Bed.Indexers.Indexers.Add(Torznab());
        var error = await Assert.ThrowsAsync<IndexerException>(() => Grab().GrabAsync(Decision(Release("Generated.Html.2018", "html"))));
        Assert.Contains("did not return a .torrent file", error.Message, StringComparison.Ordinal);
        Assert.Single(_bed.Bed.Indexers.Failures);
        Assert.Empty(_bed.Client.Added);
    }

    [Fact]
    public async Task Without_a_torrent_client_the_indexer_is_not_asked()
    {
        _bed.Bed.Indexers.Indexers.Add(Torznab());
        _bed.ClientRows.Delete(_bed.ClientId);
        var error = await Assert.ThrowsAsync<LibraryException>(() => Grab().GrabAsync(Decision(Release("Generated.NoClient"))));
        Assert.Contains("No torrent client", error.Message, StringComparison.Ordinal);
        Assert.Empty(_bed.Bed.Client.Downloads);
    }

    [Fact]
    public async Task Torrent_grabs_count_in_the_indexer_history_and_limits()
    {
        var indexerHistory = new IndexerHistoryRepository(_bed.Bed.Database);
        var tracked = new TrackedIndexerClient(_bed.Bed.Client, indexerHistory, _bed.Bed.Time, NullLogger<TrackedIndexerClient>.Instance);

        // The indexer history belongs to a saved indexer.
        var id = new IndexerRepository(_bed.Bed.Database).Add(Torznab(), DateTime.UtcNow);
        _bed.Bed.Indexers.Indexers.Add(Torznab(id) with { GrabLimit = 1 });
        await Grab(tracked).GrabAsync(Decision(Release("Generated.Counted.2017") with { IndexerId = id }));
        Assert.Equal(1, indexerHistory.CountGrabsSince(id, _bed.Bed.Time.Now.UtcDateTime.AddHours(-1)));

        var limited = await Assert.ThrowsAsync<IndexerException>(() =>
            Grab(tracked).GrabAsync(Decision(Release("Generated.Limited.2017") with { IndexerId = id })));
        Assert.Contains("Grab limit reached", limited.Message, StringComparison.Ordinal);
        Assert.Single(_bed.Client.Added);
    }

    public void Dispose() => _bed.Dispose();
}
