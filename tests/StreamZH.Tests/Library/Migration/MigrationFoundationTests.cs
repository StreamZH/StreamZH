// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Migration;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Migration;

public sealed class MigrationFoundationTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    [Theory]
    [InlineData("/tv/The Simpsons", "/data/media/tv/The Simpsons")]
    [InlineData("/tv", "/data/media/tv")]
    [InlineData("/tvshows/X", "/tvshows/X")]
    [InlineData("/movies/anime/Akira (1988)", "/data/anime/Akira (1988)")]
    [InlineData("/movies/Dune (2021)/Dune.mkv", "/data/media/movies/Dune (2021)/Dune.mkv")]
    public void Path_mapper_uses_the_longest_matching_mapping(string remote, string expected)
    {
        var mapper = new PathMapper(
        [
            new PathMapping("/tv/", "/data/media/tv"),
            new PathMapping("/movies", "/data/media/movies"),
            new PathMapping("/movies/anime", "/data/anime"),
        ]);

        Assert.Equal(expected, mapper.Map(remote));
    }

    [Fact]
    public void Path_mapper_maps_linux_paths_to_windows_and_checks_them()
    {
        var mapper = new PathMapper([new PathMapping("/media/tv", @"D:\TV Shows")], path => path == @"D:\TV Shows");
        Assert.Equal(@"D:\TV Shows\Crystal Lake\Season 01", mapper.Map("/media/tv/Crystal Lake/Season 01"));

        mapper.Use("/media/tv/");
        mapper.Use("/downloads");
        var checks = mapper.Checks();
        Assert.Equal([new MigrationPathCheck("/downloads", "/downloads", false, false), new MigrationPathCheck("/media/tv", @"D:\TV Shows", true, true)], checks);
    }

    [Fact]
    public void Validate_reports_missing_url_and_key_at_their_fields()
    {
        var error = Assert.Throws<ValidationFailedException>(() => ArrApiClient.Validate(MigrationSources.Radarr, " ", null));
        Assert.Contains("url", error.Errors.Keys);
        Assert.Contains("apiKey", error.Errors.Keys);

        var ok = ArrApiClient.Validate(MigrationSources.Sonarr, " http://192.168.1.10:8989/ ", " key ");
        Assert.Equal(new MigrationConnection("http://192.168.1.10:8989", "key"), ok);
    }

    [Fact]
    public async Task Preview_lists_sections_in_order_and_a_run_reports_every_section()
    {
        var service = Service(new FakeSection("profiles", 100, MigrationAction.Create), new FakeSection("movies", 500, MigrationAction.Link));

        var preview = await service.PreviewAsync(MigrationSources.Radarr, Request(), CancellationToken.None);
        Assert.Equal(["profiles", "movies"], preview.Sections.Select(s => s.Key));
        Assert.Equal(new MigrationCounts(2, 0, 0, 0), preview.Sections[0].Counts);
        Assert.Contains(preview.Paths, p => p.Remote == "/movies");

        var run = await service.StartAsync(MigrationSources.Radarr, Request() with { Exclude = ["movies:2"] }, "johndoe", CancellationToken.None);
        var done = await WaitAsync(service, run.Id);

        Assert.Equal(MigrationRunStatus.Done, done.Status);
        Assert.Equal(1, done.Progress);
        Assert.Equal(2, done.Report.Sections[0].Created);
        Assert.Equal(1, done.Report.Sections[1].Linked);
        Assert.Equal(1, done.Report.Sections[1].Skipped);

        // Stored: readable after the live run is gone.
        var stored = Assert.Single(service.Runs());
        Assert.Equal(MigrationRunStatus.Done, stored.Status);
        Assert.Equal("Radarr", stored.App?.AppName);
    }

    [Fact]
    public async Task A_dry_run_changes_nothing_and_reports_the_preview()
    {
        var section = new FakeSection("profiles", 100, MigrationAction.Update);
        var service = Service(section);

        var run = await service.StartAsync(MigrationSources.Radarr, Request() with { DryRun = true }, null, CancellationToken.None);
        var done = await WaitAsync(service, run.Id);

        Assert.True(done.DryRun);
        Assert.Equal(2, done.Report.Sections[0].Updated);
        Assert.Equal(0, section.Runs);
    }

    [Fact]
    public async Task A_failing_section_is_reported_and_the_rest_still_runs()
    {
        var service = Service(new FakeSection("profiles", 100, MigrationAction.Create) { Throws = true }, new FakeSection("movies", 500, MigrationAction.Create));

        var run = await service.StartAsync(MigrationSources.Radarr, Request(), null, CancellationToken.None);
        var done = await WaitAsync(service, run.Id);

        Assert.Equal(MigrationRunStatus.Done, done.Status);
        Assert.Equal(1, done.Report.Sections[0].Failed);
        Assert.Equal(2, done.Report.Sections[1].Created);
    }

    [Fact]
    public async Task Starting_without_any_section_is_a_field_error()
    {
        var service = Service(new FakeSection("history", 800, MigrationAction.Create) { Default = false });

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.StartAsync(MigrationSources.Radarr, Request(), null, CancellationToken.None));
        Assert.Contains("sections", error.Errors.Keys);
    }

    public void Dispose() => _bed.Dispose();

    private static MigrationRequest Request() => new("http://radarr:7878", "key", [new PathMapping("/movies", "/media/movies")]);

    private MigrationService Service(params IMigrationSection[] sections) =>
        new(sections, [new FakeProbe()], new MigrationRunRepository(_bed.Database), new ServiceCollection().BuildServiceProvider(),
            new InProcessEventBus(), NullLogger<MigrationService>.Instance);

    private static async Task<MigrationRun> WaitAsync(MigrationService service, int id)
    {
        for (var i = 0; i < 200; i++)
        {
            var run = service.Find(id)!;
            if (run.Status is MigrationRunStatus.Done or MigrationRunStatus.Failed or MigrationRunStatus.Cancelled && run.Finished is not null)
            {
                return run;
            }

            await Task.Delay(25, CancellationToken.None);
        }

        throw new TimeoutException("Migration run did not finish.");
    }

    private sealed class FakeProbe : IMigrationProbe
    {
        public bool Handles(string source) => true;

        public Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(string source, string? url, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult((ArrApiClient.Validate(source, url, apiKey), new MigrationAppInfo("Radarr", "5.26.2.10099")));
    }

    private sealed class FakeSection(string key, int order, MigrationAction action) : IMigrationSection
    {
        public int Runs { get; private set; }

        public bool Throws { get; init; }

        public bool Default { get; init; } = true;

        public string Source => MigrationSources.Radarr;

        public string Key => key;

        public string Label => key;

        public string Description => key;

        public int Order => order;

        public bool DefaultSelected => Default;

        public bool Advanced => false;

        public Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
        {
            context.Paths.Use("/movies");
            return Task.FromResult(new MigrationSectionPlan([new MigrationItem($"{key}:1", "One", action), new MigrationItem($"{key}:2", "Two", action)]));
        }

        public Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
        {
            Runs++;
            if (Throws)
            {
                throw new InvalidOperationException("Radarr answered nonsense.");
            }

            foreach (var item in new[] { $"{key}:1", $"{key}:2" })
            {
                if (context.IsExcluded(item))
                {
                    run.Skipped(item, "Left out.");
                }
                else if (action == MigrationAction.Link)
                {
                    run.Linked(item);
                }
                else
                {
                    run.Created(item);
                }
            }

            run.Progress(2, 2);
            return Task.CompletedTask;
        }
    }
}
