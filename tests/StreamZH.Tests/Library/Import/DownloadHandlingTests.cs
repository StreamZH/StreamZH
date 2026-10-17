// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Import;

/// <summary>Completed and failed download handling end to end: event → job → import or blocklist → new search.</summary>
public sealed class DownloadHandlingTests : IDisposable
{
    private readonly ImportTestBed _bed = new();

    private AutomaticSearchService Automatic()
    {
        var bed = _bed.Bed;
        var targets = new DecisionTargetFactory(bed.Profiles, _bed.Blocklist, _bed.Details, _bed.Settings);
        var search = new ReleaseSearchService(
            bed.Indexers, bed.Client, bed.Movies, bed.Series, targets, bed.Definitions, bed.Cache, NullLogger<ReleaseSearchService>.Instance, bed.Time);
        return new AutomaticSearchService(search, bed.Grab(), bed.Movies, bed.Series, bed.History, bed.Downloads, bed.Events,
            NullLogger<AutomaticSearchService>.Instance, _bed.Queue, bed.Time);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_bed.Bed.Series);
        services.AddScoped(_ => _bed.Service());
        services.AddScoped(_ => new FailedDownloadHandler(_bed.Settings, _bed.Blocklist, NullLogger<FailedDownloadHandler>.Instance, null, _bed.Bed.Time));
        services.AddScoped(_ => Automatic());
        return services.BuildServiceProvider();
    }

    private static async Task RunJobsAsync(JobQueue jobs, IServiceProvider services)
    {
        while (jobs.Reader.TryRead(out var job))
        {
            await jobs.StartedAsync(job);
            using var scope = services.CreateScope();
            await job.Run(scope.ServiceProvider, CancellationToken.None);
            await jobs.FinishedAsync(true);
        }
    }

    private ImportEventHandler Handler(JobQueue jobs, IServiceProvider services) =>
        new(_bed.Bed.Events, jobs, _bed.Settings, _bed.Imports, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ImportEventHandler>.Instance, _bed.Queue);

    [Fact]
    public async Task A_completed_download_is_queued_and_imported()
    {
        using var services = Services();
        var jobs = new JobQueue(_bed.Bed.Events);
        var handler = Handler(jobs, services);
        await handler.StartAsync(CancellationToken.None);

        var item = _bed.Completed(1, "Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS", _bed.MovieLink, ("dune.mkv", 50));
        await _bed.Bed.Events.PublishAsync(new DownloadFinished(item));

        Assert.Equal(ImportState.Queued, _bed.Imports.Find(1)!.State);
        Assert.Equal(1, jobs.Status.Queued);
        await RunJobsAsync(jobs, services);

        Assert.Equal(ImportState.Imported, _bed.Imports.Find(1)!.State);
        Assert.NotNull(_bed.Dune.File);
        await handler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_completed_download_handling_nothing_is_imported()
    {
        _bed.Configure(v => v with { CompletedDownloadHandling = false });
        using var services = Services();
        var jobs = new JobQueue(_bed.Bed.Events);
        await Handler(jobs, services).StartAsync(CancellationToken.None);

        var item = _bed.Completed(1, "Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS", _bed.MovieLink, ("dune.mkv", 50));
        await _bed.Bed.Events.PublishAsync(new DownloadFinished(item));

        Assert.Null(_bed.Imports.Find(1));
        Assert.Equal(0, jobs.Status.Queued);
    }

    [Fact]
    public async Task Downloads_from_the_search_page_are_not_imported_automatically()
    {
        using var services = Services();
        var jobs = new JobQueue(_bed.Bed.Events);
        await Handler(jobs, services).StartAsync(CancellationToken.None);

        var item = _bed.Completed(1, "Some.Release.1080p", MediaLink.None, ("x.mkv", 50));
        await _bed.Bed.Events.PublishAsync(new DownloadFinished(item));

        Assert.Equal(0, jobs.Status.Queued);
    }

    [Fact]
    public async Task Imports_that_were_queued_when_StreamZH_stopped_run_again()
    {
        var item = _bed.Completed(1, "Dune.2021.German.DL.1080p.BluRay.x264-DETAiLS", _bed.MovieLink, ("dune.mkv", 50));
        _bed.Service().MarkQueued(item);
        using var services = Services();
        var jobs = new JobQueue(_bed.Bed.Events);

        await Handler(jobs, services).StartAsync(CancellationToken.None);
        await RunJobsAsync(jobs, services);

        Assert.Equal(ImportState.Imported, _bed.Imports.Find(1)!.State);
    }

    [Fact]
    public async Task A_failed_download_is_blocklisted_and_the_movie_is_searched_again()
    {
        var bed = _bed.Bed;
        bed.AddIndexer(1, "Alpha");
        bed.Client.Returns(1,
            bed.Release("Dune.2021.German.DL.1080p.BluRay.x264-GRP", 12),
            bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6));

        using var services = Services();
        var jobs = new JobQueue(bed.Events);
        await Handler(jobs, services).StartAsync(CancellationToken.None);

        var failed = new DownloadItem
        {
            Id = 3, Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP", Status = DownloadStatus.Failed, Link = _bed.MovieLink,
            Error = "300 articles are missing", Indexer = "Alpha", TotalBytes = 12 * SearchTestBed.Gb,
        };
        await bed.Events.PublishAsync(new DownloadFinished(failed));
        await RunJobsAsync(jobs, services);

        var entry = Assert.Single(_bed.Blocklist.Page(1, 10).Records);
        Assert.Equal("Dune.2021.German.DL.1080p.BluRay.x264-GRP", entry.SourceTitle);
        Assert.Equal(bed.Dune.Id, entry.MovieId);
        Assert.Equal("300 articles are missing", entry.Message);

        // The blocklisted release is skipped; the next best one is grabbed.
        Assert.Equal(["Dune.2021.German.DL.1080p.WEB.H264-GRP.nzb"], bed.NzbFiles());
    }

    [Fact]
    public async Task Without_redownload_failed_there_is_no_new_search()
    {
        _bed.Configure(v => v with { RedownloadFailed = false });
        var bed = _bed.Bed;
        bed.AddIndexer(1, "Alpha");
        bed.Client.Returns(1, bed.Release("Dune.2021.German.DL.1080p.WEB.H264-GRP", 6));
        using var services = Services();
        var jobs = new JobQueue(bed.Events);
        await Handler(jobs, services).StartAsync(CancellationToken.None);

        var failed = new DownloadItem { Id = 3, Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP", Status = DownloadStatus.Failed, Link = _bed.MovieLink };
        await bed.Events.PublishAsync(new DownloadFinished(failed));
        await RunJobsAsync(jobs, services);

        Assert.Single(_bed.Blocklist.Page(1, 10).Records);
        Assert.Empty(bed.Client.Searches);
    }

    [Fact]
    public async Task A_failed_season_pack_searches_the_season_again()
    {
        var bed = _bed.Bed;
        bed.AddIndexer(1, "Alpha");
        bed.Client.Returns(1,
            bed.Release("Breaking.Bad.S01.German.DL.1080p.BluRay.x264-ROVERS", 20),
            bed.Release("Breaking.Bad.S01.German.DL.1080p.WEB.H264-OTHER", 15));
        var link = _bed.EpisodeLink([.. Enumerable.Range(1, 7).Select(e => (1, e))]);
        var failed = new DownloadItem { Id = 4, Title = "Breaking.Bad.S01.German.DL.1080p.BluRay.x264-ROVERS", Status = DownloadStatus.Failed, Link = link };

        var handler = new FailedDownloadHandler(_bed.Settings, _bed.Blocklist, NullLogger<FailedDownloadHandler>.Instance, null, bed.Time);
        Assert.True(await handler.HandleAsync(failed));
        await FailedDownloadHandler.SearchAgainAsync(failed, Automatic(), bed.Series, CancellationToken.None);

        Assert.Equal(["Breaking.Bad.S01.German.DL.1080p.WEB.H264-OTHER.nzb"], bed.NzbFiles());
        var criteria = Assert.IsType<EpisodeSearchCriteria>(bed.Client.Searches.Single().Criteria);
        Assert.Empty(criteria.EpisodeNumbers);
        Assert.Equal(1, criteria.SeasonNumber);
    }

    [Fact]
    public void Blocklist_feeds_the_decisions_for_movies_and_series()
    {
        _bed.Blocklist.Add(new BlocklistItem { SourceTitle = "Dune.2021.1080p.BluRay.x264-BAD", MediaType = MediaType.Movie, MovieId = _bed.Bed.Dune.Id });
        _bed.Blocklist.Add(new BlocklistItem { SourceTitle = "Breaking.Bad.S01E01.720p-BAD", MediaType = MediaType.Series, SeriesId = _bed.Bed.Show.Id });
        var targets = new DecisionTargetFactory(_bed.Bed.Profiles, _bed.Blocklist, _bed.Details, _bed.Settings);

        Assert.Contains("dune.2021.1080p.bluray.x264-bad", targets.ForMovie(_bed.Dune).BlocklistedTitles);
        Assert.Contains("Breaking.Bad.S01E01.720p-BAD", targets.ForEpisodes(_bed.Show, [_bed.Bed.Episode(1, 1)]).BlocklistedTitles);

        _bed.Configure(v => v with { PropersAndRepacks = ProperHandling.DoNotUpgrade });
        Assert.Equal(ProperHandling.DoNotUpgrade, targets.ForMovie(_bed.Dune).Propers);
    }

    [Fact]
    public void Stored_quality_survives_a_rename()
    {
        var path = Path.Combine(_bed.Dune.FolderPath, "Dune (2021).mkv");
        _bed.Details.Save(new MediaFileDetails(path, StreamZH.Core.Quality.Quality.Remux2160p.Id, 2, 0, false, [], "GRP", "x", null, null, null, DateTime.UtcNow));
        var movie = _bed.Dune;
        movie.File = new MediaFile("Dune (2021).mkv", 1, DateTime.UtcNow);

        var target = new DecisionTargetFactory(_bed.Bed.Profiles, _bed.Blocklist, _bed.Details, _bed.Settings).ForMovie(movie);

        Assert.Equal(StreamZH.Core.Quality.Quality.Remux2160p, target.ExistingQuality!.Quality);
        Assert.Equal(2, target.ExistingQuality.Revision.Version);
    }

    public void Dispose() => _bed.Dispose();
}
