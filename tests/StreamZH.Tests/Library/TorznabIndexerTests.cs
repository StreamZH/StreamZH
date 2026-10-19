// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Indexers;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library;

/// <summary>0.11: Torznab indexers in the indexer service and repository, the catalog's torrent entries and seed goals.</summary>
public sealed class TorznabIndexerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly IndexerService _service;

    public TorznabIndexerTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _service = new IndexerService(new IndexerRepository(database), new IndexerStatusRepository(database), TimeProvider.System, NullLogger<IndexerService>.Instance);
    }

    private static IndexerDefinition Torznab(string name = "Tracker") => new(
        0, name, "torznab", "https://jackett.example:9117/api/v2.0/indexers/exampletracker/results/torznab/", "/api", "key", [2000, 2040], [5040], 25, true, true, true);

    [Fact]
    public void Seed_settings_are_stored_and_read_back()
    {
        var created = _service.Create(Torznab() with { MinimumSeeders = 5, SeedRatio = 1.5, SeedTimeMinutes = 4320, SeasonPackSeedTimeMinutes = 10080, DefinitionKey = "generic-torznab" });

        var stored = _service.Find(created.Id)!;
        Assert.Equal((IndexerDefinition.Torznab, DownloadProtocol.Torrent), (stored.Implementation, stored.Protocol));
        Assert.Equal("https://jackett.example:9117/api/v2.0/indexers/exampletracker/results/torznab", stored.BaseUrl);
        Assert.Equal((5, 1.5, 4320, 10080), (stored.MinimumSeeders, stored.SeedRatio!.Value, stored.SeedTimeMinutes!.Value, stored.SeasonPackSeedTimeMinutes!.Value));

        var cleared = _service.Update(created.Id, stored with { MinimumSeeders = 0, SeedRatio = null, SeedTimeMinutes = null, SeasonPackSeedTimeMinutes = null });
        Assert.Equal((0, (double?)null, (int?)null, (int?)null), (cleared.MinimumSeeders, cleared.SeedRatio, cleared.SeedTimeMinutes, cleared.SeasonPackSeedTimeMinutes));
        Assert.Equal(IndexerCatalog.GenericTorznabKey, _service.Details(created.Id)!.Definition!.Key);
    }

    [Fact]
    public void Seed_settings_are_validated_and_ignored_for_usenet()
    {
        var errors = Assert.Throws<ValidationFailedException>(() => _service.Create(Torznab() with
        {
            MinimumSeeders = -1,
            SeedRatio = 0,
            SeedTimeMinutes = 0,
            SeasonPackSeedTimeMinutes = 600_000,
        })).Errors;
        Assert.Equal(["minimumSeeders", "seasonPackSeedTimeMinutes", "seedRatio", "seedTimeMinutes"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("download client's setting", errors["seedRatio"][0], StringComparison.Ordinal);

        Assert.Equal(["seedRatio"], Assert.Throws<ValidationFailedException>(() => _service.Create(Torznab() with { SeedRatio = double.NaN })).Errors.Keys);

        var usenet = _service.Create(Torznab("Usenet") with { Implementation = "newznab", MinimumSeeders = -1, SeedRatio = -2 });
        Assert.Equal((IndexerDefinition.Newznab, 1, (double?)null), (usenet.Implementation, usenet.MinimumSeeders, usenet.SeedRatio));
    }

    [Fact]
    public void Jackett_all_endpoint_and_mismatched_definitions_are_rejected()
    {
        var all = Assert.Throws<ValidationFailedException>(() => _service.Create(Torznab() with
        {
            BaseUrl = "http://jackett.example:9117/api/v2.0/indexers/all/results/torznab",
        })).Errors;
        Assert.Contains("'all' endpoint", all["baseUrl"][0], StringComparison.Ordinal);

        var wrongKind = Assert.Throws<ValidationFailedException>(() => _service.Create(Torznab() with { DefinitionKey = "nzbgeek" })).Errors;
        Assert.Equal("NZBgeek is a Newznab indexer.", wrongKind["definitionKey"][0]);
    }

    [Fact]
    public async Task Test_fails_when_the_indexer_offers_none_of_the_selected_categories()
    {
        var client = new StubIndexerClient();

        var none = await _service.TestAsync(Torznab() with { Categories = [2030], TvCategories = [5030] }, client, CancellationToken.None);
        Assert.False(none.Success);
        Assert.Equal("This indexer supports none of the selected categories (2030, 5030). Choose categories it offers.", none.Error);

        // One supported category is enough (subcategories count), and Newznab indexers are not checked (Radarr checks Torznab only).
        Assert.True((await _service.TestAsync(Torznab() with { Categories = [2030, 2045], TvCategories = [] }, client, CancellationToken.None)).Success);
        Assert.True((await _service.TestAsync(Torznab() with { Implementation = "Newznab", Categories = [2030] }, client, CancellationToken.None)).Success);
    }

    [Fact]
    public void Schema_for_torznab()
    {
        var schema = IndexerService.Schema("TORZNAB");
        Assert.Equal((IndexerDefinition.Torznab, DownloadProtocol.Torrent, 1), (schema.Implementation, schema.Protocol, schema.MinimumSeeders));
        Assert.Equal(IndexerDefinition.Newznab, IndexerService.Schema("Cardigann").Implementation);
    }

    [Fact]
    public void Catalog_has_torrent_presets_and_matches_by_protocol()
    {
        var generic = IndexerCatalog.Find(IndexerCatalog.GenericTorznabKey)!;
        Assert.Equal((IndexerDefinition.Torznab, IndexerProtocol.Torrent, string.Empty), (generic.Implementation, generic.Protocol, generic.BaseUrl));
        Assert.Equal(("/api/torznab/api", "de-DE"), (IndexerCatalog.Find("torrent-network")!.ApiPath, IndexerCatalog.Find("torrent-network")!.Language));
        Assert.Equal(IndexerPrivacy.Public, IndexerCatalog.Find("animetosho-torznab")!.Privacy);

        Assert.Equal("animetosho-torznab", IndexerCatalog.Match(null, "https://feed.animetosho.org/api", IndexerProtocol.Torrent).Key);
        Assert.Equal("animetosho-usenet", IndexerCatalog.Match(null, "https://feed.animetosho.org/api").Key);
        Assert.Equal(IndexerCatalog.GenericTorznabKey, IndexerCatalog.Match("NZBgeek", null, IndexerProtocol.Torrent).Key);
        Assert.Equal(IndexerCatalog.GenericTorznabKey, IndexerCatalog.For(Torznab())!.Key);
    }

    [Fact]
    public void Seed_goals_come_from_the_indexer_and_the_trackers_minimums()
    {
        var indexer = Torznab() with { Implementation = IndexerDefinition.Torznab, SeedRatio = 1.0, SeedTimeMinutes = 60, SeasonPackSeedTimeMinutes = 600 };
        ReleaseDecision Decision(string title, double? minRatio = null, long? minSeconds = null, bool fullSeason = false)
        {
            var release = new ReleaseInfo { Guid = "g", Title = title, DownloadUrl = "magnet:?xt=urn:btih:abc", Protocol = DownloadProtocol.Torrent, MinimumRatio = minRatio, MinimumSeedTimeSeconds = minSeconds };
            var parsed = new ParsedRelease { ReleaseTitle = title, Title = "Show", CleanTitle = "show", SeasonNumber = fullSeason ? 1 : null, FullSeason = fullSeason };
            var candidate = new Core.Decisions.ReleaseCandidate(parsed, 0, "Tracker", 25, default, release.DownloadUrl, "g");
            return new ReleaseDecision(release, parsed, new Core.Decisions.Decision(candidate, []), new SearchSubject(MediaType.Movie, 1, null, "x"), []);
        }

        Assert.Equal(new SeedGoal(1.0, 60), SeedCriteria.For(indexer, Decision("Some.Movie.2020.1080p")));
        Assert.Equal(new SeedGoal(1.0, 600), SeedCriteria.For(indexer, Decision("Show.S01.1080p", fullSeason: true)));
        Assert.Equal(new SeedGoal(1.5, 2880), SeedCriteria.For(indexer, Decision("Some.Movie.2020.1080p", minRatio: 1.5, minSeconds: 172_800)));
        Assert.Equal(new SeedGoal(null, 1), SeedCriteria.For(indexer with { SeedRatio = null, SeedTimeMinutes = null }, Decision("Some.Movie.2020.1080p", minSeconds: 30)));
        Assert.Equal(SeedGoal.None, SeedCriteria.For(indexer with { Implementation = IndexerDefinition.Newznab }, Decision("Some.Movie.2020.1080p", minRatio: 2)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
