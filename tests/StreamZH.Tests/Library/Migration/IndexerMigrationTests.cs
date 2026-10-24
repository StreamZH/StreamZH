// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Indexers;
using StreamZH.Library.Indexers;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Indexers;

namespace StreamZH.Tests.Library.Migration;

public sealed class IndexerMigrationTests : IAsyncLifetime
{
    private const string Radarr = MigrationSources.Radarr;
    private const string Sonarr = MigrationSources.Sonarr;

    private readonly MigrationBed _bed = new();
    private readonly ArrIndexersSection _radarr = new(Radarr);
    private readonly ArrIndexersSection _sonarr = new(Sonarr);

    [Fact]
    public async Task Radarr_preview_takes_newznab_and_torznab_indexers_and_explains_the_rest()
    {
        var plan = await _radarr.PreviewAsync(_bed.Context(Radarr), CancellationToken.None);
        var items = plan.Items.ToDictionary(i => i.Key);

        var geek = items["indexers:1"];
        Assert.Equal((MigrationAction.Create, true), (geek.Action, geek.NeedsSecret));
        Assert.Equal("NZBgeek · https://api.nzbgeek.info · movie categories 2030, 2040, 2045 · priority 10", geek.Detail);
        Assert.Contains("without it the indexer is added switched off", geek.Warning, StringComparison.Ordinal);

        Assert.Equal(MigrationAction.Create, items["indexers:2"].Action);
        Assert.Contains("take it over from Prowlarr", items["indexers:3"].Reason, StringComparison.Ordinal);
        var torznab = items["indexers:4"];
        Assert.Equal((MigrationAction.Create, true), (torznab.Action, torznab.NeedsSecret));
        Assert.Equal(
            "torrent · https://torznab.example/api/v2.0/indexers/tracker/results/torznab/ · movie categories 2000, 2040 · priority 25 · minimum 3 seeders, seed ratio 1.5, seed time 4320 min",
            torznab.Detail);
        Assert.Equal("Same indexer as \"NZBgeek\" in Radarr.", items["indexers:5"].Reason);
        Assert.Contains("Only Newznab and Torznab", items["indexers:6"].Reason, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings!, w => w.Contains("Prowlarr", StringComparison.Ordinal));
        Assert.Empty(_bed.Indexers.All());
    }

    [Fact]
    public async Task The_same_indexer_from_radarr_and_sonarr_becomes_one_with_both_categories()
    {
        // Radarr first, without keys: added switched off.
        var first = _bed.Run();
        await _radarr.RunAsync(_bed.Context(Radarr, _bed.Request(Radarr) with { Exclude = ["indexers:2", "indexers:4"] }), first, CancellationToken.None);
        Assert.Equal((1, 5), (first.Report().Created, first.Report().Skipped));
        var geek = Assert.Single(_bed.Indexers.All());
        Assert.Equal(("NZBgeek", "https://api.nzbgeek.info", "/api", "", false, "nzbgeek"), (geek.Name, geek.BaseUrl, geek.ApiPath, geek.ApiKey, geek.Enable, geek.DefinitionKey));
        Assert.Equal([2030, 2040, 2045], geek.Categories);
        Assert.Empty(geek.TvCategories);
        Assert.Contains(first.Report().Messages, m => m.Level == MigrationMessageLevel.Warn && m.Text.Contains("switched off", StringComparison.Ordinal));

        // Sonarr with the key: the same indexer gets the TV and anime categories and is switched on.
        var preview = await _sonarr.PreviewAsync(_bed.Context(Sonarr), CancellationToken.None);
        Assert.Equal((MigrationAction.Update, true), (preview.Items[0].Action, preview.Items[0].NeedsSecret));
        Assert.Contains("adds the TV categories to the existing indexer", preview.Items[0].Detail, StringComparison.Ordinal);

        var second = _bed.Run();
        var secrets = new Dictionary<string, string> { ["indexers:4"] = " geek-key " };
        await _sonarr.RunAsync(_bed.Context(Sonarr, _bed.Request(Sonarr) with { Secrets = secrets }), second, CancellationToken.None);
        Assert.Equal(1, second.Report().Updated);
        geek = Assert.Single(_bed.Indexers.All());
        Assert.Equal(("geek-key", true, 12), (geek.ApiKey, geek.Enable, geek.Priority));
        Assert.Equal([2030, 2040, 2045], geek.Categories);
        Assert.Equal([5030, 5040, 5070], geek.TvCategories);
        Assert.Equal((true, true, true), (geek.EnableRss, geek.EnableAutomaticSearch, geek.EnableInteractiveSearch));

        // Radarr again: updated, not duplicated, the key stays; "My Indexer" and the Torznab indexer are new.
        var third = _bed.Run();
        await _radarr.RunAsync(_bed.Context(Radarr), third, CancellationToken.None);
        Assert.Equal((2, 1), (third.Report().Created, third.Report().Updated));
        Assert.Equal(3, _bed.Indexers.All().Count);
        Assert.Equal("geek-key", _bed.Indexers.All().Single(i => i.Name == "NZBgeek").ApiKey);
        var own = _bed.Indexers.All().Single(i => i.Name == "My Indexer");
        Assert.Equal(("https://indexer.example.org", "&extended=1", IndexerCatalog.GenericNewznabKey), (own.BaseUrl, own.AdditionalParameters, own.DefinitionKey));
    }

    [Fact]
    public async Task An_indexer_already_in_streamzh_is_matched_by_its_url()
    {
        var mine = _bed.Indexers.Create(new IndexerDefinition(0, "geek", IndexerDefinition.Newznab, "https://api.nzbgeek.info/", "/api", "stored", [2000], [5000], 5, true, false, true));

        var plan = await _radarr.PreviewAsync(_bed.Context(Radarr), CancellationToken.None);
        var item = plan.Items.Single(i => i.Key == "indexers:1");
        Assert.Equal((MigrationAction.Update, false), (item.Action, item.NeedsSecret));
        Assert.Contains("to \"geek\"", item.Detail, StringComparison.Ordinal);

        await _radarr.RunAsync(_bed.Context(Radarr, _bed.Request(Radarr) with { Exclude = ["indexers:2", "indexers:4"] }), _bed.Run(), CancellationToken.None);
        var geek = Assert.Single(_bed.Indexers.All());
        Assert.Equal((mine.Id, "geek", "stored", true), (geek.Id, geek.Name, geek.ApiKey, geek.Enable));
        Assert.Equal([2030, 2040, 2045], geek.Categories);
        Assert.Equal([5000], geek.TvCategories);
    }

    [Fact]
    public async Task A_torznab_indexer_comes_with_its_seed_settings_and_the_typed_key()
    {
        var request = _bed.Request(Radarr) with
        {
            Exclude = ["indexers:1", "indexers:2"],
            Secrets = new Dictionary<string, string> { ["indexers:4"] = "tracker-key" },
        };
        var run = _bed.Run();
        await _radarr.RunAsync(_bed.Context(Radarr, request), run, CancellationToken.None);

        var torznab = Assert.Single(_bed.Indexers.All());
        Assert.Equal(("Some Torznab", IndexerDefinition.Torznab, Core.Downloads.DownloadProtocol.Torrent, "tracker-key", true),
            (torznab.Name, torznab.Implementation, torznab.Protocol, torznab.ApiKey, torznab.Enable));
        Assert.Equal(("https://torznab.example/api/v2.0/indexers/tracker/results/torznab", "/api", IndexerCatalog.GenericTorznabKey),
            (torznab.BaseUrl, torznab.ApiPath, torznab.DefinitionKey));
        Assert.Equal([2000, 2040], torznab.Categories);
        Assert.Equal((3, 1.5, 4320, (int?)null), (torznab.MinimumSeeders, torznab.SeedRatio!.Value, torznab.SeedTimeMinutes!.Value, torznab.SeasonPackSeedTimeMinutes));
    }

    [Fact]
    public async Task Download_clients_point_to_sabnzbd_and_take_over_torrent_clients()
    {
        var section = new ArrDownloadClientsSection(Sonarr);
        Assert.True(section.DefaultSelected);
        Assert.False(section.Advanced);

        var plan = await section.PreviewAsync(_bed.Context(Sonarr), CancellationToken.None);

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(("SABnzbd at 192.168.1.10:8080, category tv", "http://192.168.1.10:8080/sabnzbd", MigrationAction.Skip),
            (plan.Items[0].Label, plan.Items[0].Detail, plan.Items[0].Action));
        Assert.Equal(("qBittorrent (qBittorrent)", MigrationAction.Create), (plan.Items[1].Label, plan.Items[1].Action));

        var run = _bed.Run();
        await section.RunAsync(_bed.Context(Sonarr), run, CancellationToken.None);
        Assert.Equal((1, 1), (run.Report().Created, run.Report().Skipped));
    }

    [Fact]
    public async Task Prowlarr_indexers_reuse_the_prowlarr_import()
    {
        var section = new ProwlarrIndexersSection();
        _bed.Indexers.Create(new IndexerDefinition(0, "Keyless", "Newznab", "https://x.example", "/api", "k", [2000], [], 25, true, true, true));

        var plan = await section.PreviewAsync(_bed.Context(MigrationSources.Prowlarr), CancellationToken.None);
        Assert.Equal(["indexers:1", "indexers:2", "indexers:3", "indexers:4", "indexers:5", "indexers:6"], plan.Items.Select(i => i.Key));
        Assert.Equal(
            [MigrationAction.Create, MigrationAction.Create, MigrationAction.Link, MigrationAction.Create, MigrationAction.Create, MigrationAction.Create],
            plan.Items.Select(i => i.Action));
        Assert.Equal((true, false), (plan.Items[3].NeedsSecret, plan.Items[4].NeedsSecret));
        Assert.True(plan.Items[0].NeedsSecret);
        Assert.Contains("https://api.nzbgeek.info/api", plan.Items[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("prowlarr", plan.Items[0].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("through Prowlarr", plan.Items[4].Detail, StringComparison.Ordinal);
        Assert.False(plan.Items[5].NeedsSecret);
        Assert.Contains(plan.Warnings!, w => w.Contains("switched off", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings!, w => w.Contains("keep Prowlarr running", StringComparison.Ordinal));

        var request = _bed.Request(MigrationSources.Prowlarr) with
        {
            Secrets = new Dictionary<string, string> { ["indexers:1"] = "own-geek-key" },
            Exclude = ["indexers:6"],
        };
        var run = _bed.Run();
        await section.RunAsync(_bed.Context(MigrationSources.Prowlarr, request), run, CancellationToken.None);
        var report = run.Report();
        Assert.Equal((4, 1, 1), (report.Created, report.Linked, report.Skipped));
        var torznab = _bed.Indexers.All().Single(i => i.Name == "Some Torznab");
        Assert.Equal((Core.Downloads.DownloadProtocol.Torrent, 3, 1.5), (torznab.Protocol, torznab.MinimumSeeders, torznab.SeedRatio!.Value));
        var geek = _bed.Indexers.All().Single(i => i.Name == "NZBgeek");
        Assert.Equal(("https://api.nzbgeek.info", "own-geek-key", true), (geek.BaseUrl, geek.ApiKey, geek.Enable));
        Assert.Equal(("https://torznab.example", string.Empty, false), (torznab.BaseUrl, torznab.ApiKey, torznab.Enable));
        Assert.Contains(report.Messages, m => m.Text.Contains("Some Torznab is switched off", StringComparison.Ordinal));

        // Again: everything is there already.
        var again = _bed.Run();
        await section.RunAsync(_bed.Context(MigrationSources.Prowlarr, request), again, CancellationToken.None);
        Assert.Equal((0, 5), (again.Report().Created, again.Report().Linked));
        Assert.Equal(5, _bed.Indexers.All().Count);
    }

    [Theory]
    [InlineData("http://prowlarr:9696/1/", "NZBgeek", true)]
    [InlineData("http://192.168.1.10:9696/12", "NZBgeek", true)]
    [InlineData("https://api.nzbgeek.info", "NZBgeek (Prowlarr)", true)]
    [InlineData("https://api.nzbgeek.info", "NZBgeek", false)]
    [InlineData("https://indexer.example.org/2", "Indexer", false)]
    public void Indexers_synced_by_prowlarr_are_recognised(string url, string name, bool expected)
    {
        var json = System.Text.Json.JsonDocument.Parse($$"""[{"id": 1, "name": "{{name}}", "implementation": "Newznab", "fields": [{"name": "baseUrl", "value": "{{url}}"}]}]""");
        Assert.Equal(expected, ArrIndexersSection.ComesFromProwlarr(ArrIndexer.Parse(json.RootElement)[0]));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _bed.DisposeAsync();
}
