// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Import;

/// <summary>Finished downloads the import can see; tests put them in directly.</summary>
public sealed class FakeDownloadQueue : IDownloadQueue
{
    public ConcurrentDictionary<int, DownloadItem> Items { get; } = new();

    public IReadOnlyList<DownloadItem> Active() =>
        [.. Items.Values.Where(i => i.Status is not (DownloadStatus.Completed or DownloadStatus.Failed))];

    public DownloadItem? Find(int id) => Items.GetValueOrDefault(id);
}

/// <summary>
/// The search test bed (database, Dune, Breaking Bad, history, profiles) plus everything the import needs, with real
/// folders in a temp directory: finished downloads under downloads/complete, the library under media.
/// </summary>
public sealed class ImportTestBed : IDisposable
{
    public const long Mb = 1024L * 1024;

    public ImportTestBed()
    {
        Settings = new MediaManagementSettings(Bed.Config);
        Imports = new DownloadImportRepository(Bed.Database);
        Blocklist = new BlocklistRepository(Bed.Database);
        Details = new MediaFileDetailsRepository(Bed.Database);
        EpisodeFiles = new EpisodeFileRepository(Bed.Database);
        Files = new ImportFileSystem(NullLogger<ImportFileSystem>.Instance);
        Bed.Events.Subscribe<MediaImported>((e, _) =>
        {
            Imported.Enqueue(e);
            return ValueTask.CompletedTask;
        });
        Bed.Events.Subscribe<MediaRenamed>((e, _) =>
        {
            Renamed.Enqueue(e);
            return ValueTask.CompletedTask;
        });
    }

    public SearchTestBed Bed { get; } = new();
    public MediaManagementSettings Settings { get; }
    public DownloadImportRepository Imports { get; }
    public BlocklistRepository Blocklist { get; }
    public MediaFileDetailsRepository Details { get; }
    public EpisodeFileRepository EpisodeFiles { get; }
    public ImportFileSystem Files { get; }
    public FakeDownloadQueue Queue { get; } = new();
    public ConcurrentQueue<MediaImported> Imported { get; } = new();
    public ConcurrentQueue<MediaRenamed> Renamed { get; } = new();

    public Movie Dune => Bed.Movies.Find(Bed.Dune.Id)!;
    public Series Show => Bed.Series.Find(Bed.Show.Id)!;
    public string Media => Path.Combine(Bed.DataDirectory, "media");

    public DownloadImportService Service() => new(
        Settings, Bed.Downloads, Bed.Movies, Bed.Series, EpisodeFiles, Details, Imports, Bed.History, Bed.Profiles, Files, Bed.Events,
        NullLogger<DownloadImportService>.Instance, Queue, null, null, Bed.Time);

    public RenameService Rename() => new(
        Settings, Bed.Movies, Bed.Series, EpisodeFiles, Details, Bed.History, Files, Bed.Events, NullLogger<RenameService>.Instance, Bed.Time);

    public void Configure(Func<MediaManagementValues, MediaManagementValues> change) => Settings.Update(change(Settings.Current));

    /// <summary>A completed download with files (sizes in MB; sparse, so big files cost nothing) in its own folder.</summary>
    public DownloadItem Completed(int id, string title, MediaLink link, params (string Path, long Mb)[] files)
    {
        var category = link.MediaType == MediaType.Series ? "series" : link.MediaType == MediaType.Movie ? "movies" : "other";
        var folder = Path.Combine(Bed.Downloads.CompleteFolder, category, title);
        foreach (var (relative, mb) in files)
        {
            WriteFile(Path.Combine(folder, relative), mb);
        }

        Directory.CreateDirectory(folder);
        var item = new DownloadItem
        {
            Id = id,
            Title = title,
            Category = link.MediaType == MediaType.Series ? DownloadCategory.Series : link.MediaType == MediaType.Movie ? DownloadCategory.Movie : DownloadCategory.Other,
            Status = DownloadStatus.Completed,
            Link = link,
            OutputPath = folder,
            TotalBytes = files.Sum(f => f.Mb) * Mb,
            DownloadedBytes = files.Sum(f => f.Mb) * Mb,
            Indexer = "Alpha",
            Added = Bed.Time.Now,
            Completed = Bed.Time.Now,
        };
        Queue.Items[id] = item;
        return item;
    }

    public MediaLink MovieLink => new(MediaType.Movie, Bed.Dune.Id, null, []);

    public MediaLink EpisodeLink(params (int Season, int Episode)[] episodes) =>
        new(MediaType.Series, null, Bed.Show.Id, [.. episodes.Select(e => Bed.Episode(e.Season, e.Episode).Id)]);

    public static void WriteFile(string path, long mb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.WriteByte(1);
        stream.SetLength(Math.Max(1, mb * Mb));
    }

    public void Dispose() => Bed.Dispose();
}
