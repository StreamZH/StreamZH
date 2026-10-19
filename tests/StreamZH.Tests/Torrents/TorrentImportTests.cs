// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Downloads.Torrent;
using StreamZH.Library.Import;
using StreamZH.Tests.Library.Import;

namespace StreamZH.Tests.Torrents;

public sealed class TorrentImportTests : IDisposable
{
    private readonly ImportTestBed _bed = new();

    private string SavePath => Path.Combine(_bed.Bed.DataDirectory, "torrents");

    /// <summary>A finished torrent still seeding in its client, with its files in the client's save folder.</summary>
    private DownloadItem Seeding(int id, string title, string output, MediaLink link) =>
        _bed.Queue.Items[id] = new DownloadItem
        {
            Id = id,
            Title = title,
            Category = link.MediaType == Core.Media.MediaType.Series ? DownloadCategory.Series : DownloadCategory.Movie,
            Status = DownloadStatus.Completed,
            Link = link,
            OutputPath = output,
            Indexer = "Tracker",
            Added = _bed.Bed.Time.Now,
            Completed = _bed.Bed.Time.Now,
            Protocol = DownloadProtocol.Torrent,
            ClientId = 1,
            ExternalId = "0123456789abcdef0123456789abcdef01234567",
            Seeding = true,
        };

    private static byte FirstByte(string path)
    {
        using var stream = File.OpenRead(path);
        return (byte)stream.ReadByte();
    }

    private static void SetFirstByte(string path, byte value)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.WriteByte(value);
    }

    [Fact]
    public async Task Seeding_torrents_are_hard_linked_never_moved_even_with_move_mode()
    {
        _bed.Configure(o => o with { ImportMode = ImportMode.Move, CopyUsingHardlinks = true });
        var folder = Path.Combine(SavePath, "Dune.2021.1080p.BluRay.x264-GRP");
        var source = Path.Combine(folder, "Dune.2021.1080p.BluRay.x264-GRP.mkv");
        ImportTestBed.WriteFile(source, 900);
        ImportTestBed.WriteFile(Path.Combine(folder, "Dune.2021.1080p.BluRay.x264-GRP.en.srt"), 1);
        var item = Seeding(1, "Dune.2021.1080p.BluRay.x264-GRP", folder, _bed.MovieLink);

        var result = await _bed.Service().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        var destination = Assert.Single(result.Files);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));

        // Same volume: a hard link (one file, two names) — writing through one name shows in the other.
        SetFirstByte(destination, 7);
        Assert.Equal(7, FirstByte(source));
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(folder, "Dune.2021.1080p.BluRay.x264-GRP.en.srt")));
    }

    [Fact]
    public async Task Without_hard_links_seeding_torrents_are_copied()
    {
        _bed.Configure(o => o with { ImportMode = ImportMode.Move, CopyUsingHardlinks = false });
        var folder = Path.Combine(SavePath, "Dune.2021.2160p.WEB-DL-GRP");
        var source = Path.Combine(folder, "Dune.2021.2160p.WEB-DL-GRP.mkv");
        ImportTestBed.WriteFile(source, 900);
        var item = Seeding(2, "Dune.2021.2160p.WEB-DL-GRP", folder, _bed.MovieLink);

        var result = await _bed.Service().ImportAsync(item.Id);

        var destination = Assert.Single(result.Files);
        SetFirstByte(destination, 9);
        Assert.Equal(1, FirstByte(source));
        Assert.Equal(new FileInfo(source).Length, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task A_single_file_torrent_imports_only_its_file_and_leaves_the_folder_alone()
    {
        _bed.Configure(o => o with { ImportMode = ImportMode.Move });
        // The torrent lives directly in the complete folder; nothing there may be deleted.
        var save = _bed.Bed.Downloads.CompleteFolder;
        var file = Path.Combine(save, "Dune.2021.720p.WEB.H264-GRP.mkv");
        var other = Path.Combine(save, "Another.Movie.2020.1080p.mkv");
        ImportTestBed.WriteFile(file, 700);
        ImportTestBed.WriteFile(other, 700);
        ImportTestBed.WriteFile(Path.Combine(save, "unrelated.srt"), 1);
        var item = Seeding(3, "Dune.2021.720p.WEB.H264-GRP", file, _bed.MovieLink);

        var candidates = _bed.Service().Candidates(item.Id, null);
        Assert.Equal("Dune.2021.720p.WEB.H264-GRP.mkv", Assert.Single(candidates).RelativePath);

        var result = await _bed.Service().ImportAsync(item.Id);
        Assert.Equal(ImportState.Imported, result.State);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(other));
        Assert.DoesNotContain(Directory.EnumerateFiles(_bed.Dune.FolderPath), f => f.EndsWith(".srt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_path_streamzh_cannot_see_points_to_remote_path_mappings()
    {
        var item = Seeding(4, "Dune.2021.1080p.WEB-GRP", "/remote/only/Dune.2021.1080p.WEB-GRP", _bed.MovieLink);
        var result = await _bed.Service().ImportAsync(item.Id);
        Assert.Equal(ImportState.Failed, result.State);
        Assert.Contains("remote path mapping", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_paths_map_by_host_and_longest_prefix()
    {
        RemotePathMapping[] mappings =
        [
            new(1, "torrent-box", "/downloads/", "/mnt/torrents/"),
            new(2, "torrent-box", "/downloads/movies/", "/mnt/movies/"),
            new(3, "windows-box", @"D:\Torrents\", "/mnt/win/"),
        ];
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"/mnt/movies{sep}Dune{sep}a.mkv", RemotePathMappingService.Map(mappings, "Torrent-Box", "/downloads/movies/Dune/a.mkv"));
        Assert.Equal($"/mnt/torrents{sep}tv{sep}Show", RemotePathMappingService.Map(mappings, "torrent-box", "/downloads/tv/Show"));
        Assert.Equal("/mnt/torrents", RemotePathMappingService.Map(mappings, "torrent-box", "/downloads"));
        Assert.Equal($"/mnt/win{sep}Movie{sep}x.mkv", RemotePathMappingService.Map(mappings, "windows-box", @"d:\torrents\Movie\x.mkv"));
        Assert.Equal("/downloads/x", RemotePathMappingService.Map(mappings, "other-host", "/downloads/x"));
        Assert.Equal("/downloadsx/a", RemotePathMappingService.Map(mappings, "torrent-box", "/downloadsx/a"));
    }

    [Fact]
    public void Remote_path_mappings_are_validated()
    {
        var service = new RemotePathMappingService(new RemotePathMappingRepository(_bed.Bed.Database));
        Directory.CreateDirectory(SavePath);
        var created = service.Create("torrent-box", "/downloads", SavePath);
        Assert.Equal("/downloads/", created.RemotePath);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), created.LocalPath, StringComparison.Ordinal);

        var duplicate = Assert.Throws<ValidationFailedException>(() => service.Create("TORRENT-BOX", "/downloads/", SavePath));
        Assert.True(duplicate.Errors.ContainsKey("remotePath"));
        var invalid = Assert.Throws<ValidationFailedException>(() => service.Create("http://x/", "downloads", "relative"));
        Assert.Equal(["host", "localPath", "remotePath"], invalid.Errors.Keys.Order());
        var missing = Assert.Throws<ValidationFailedException>(() => service.Create("box", "/x", Path.Combine(SavePath, "nope")));
        Assert.True(missing.Errors.ContainsKey("localPath"));

        var updated = service.Update(created.Id, "torrent-box", "/data/", SavePath);
        Assert.Equal("/data/", updated.RemotePath);
        service.Delete(created.Id);
        Assert.Empty(service.All());
        Assert.Throws<LibraryException>(() => service.Delete(created.Id));
    }

    public void Dispose() => _bed.Dispose();
}
