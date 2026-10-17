// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using StreamZH.Core.Indexers;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

public sealed class GrabServiceTests : IDisposable
{
    private const string Title = "Dune.2021.German.DL.1080p.BluRay.x264-GRP";
    private readonly SearchTestBed _bed = new();

    [Fact]
    public async Task Grab_writes_the_nzb_and_records_history()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release(Title, 12));
        var result = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        var release = result.Decisions[0].Release;

        var grabbed = await _bed.Grab().GrabAsync(release.IndexerId, release.Guid);

        Assert.Equal(Path.Combine(_bed.DataDirectory, "downloads", "nzb", $"{Title}.nzb"), grabbed.NzbPath);
        Assert.Equal(FakeIndexerClient.ValidNzb, await File.ReadAllBytesAsync(grabbed.NzbPath));
        Assert.Equal("Alpha", grabbed.Indexer);
        Assert.Contains(1, _bed.Indexers.Successes);

        var (records, total) = _bed.History.Page(1, 10, movieId: _bed.Dune.Id);
        Assert.Equal(1, total);
        var entry = records[0];
        Assert.Equal(HistoryEventType.Grabbed, entry.EventType);
        Assert.Equal(MediaType.Movie, entry.MediaType);
        Assert.Equal(Title, entry.SourceTitle);
        Assert.Equal(Quality.Bluray1080p.Id, entry.QualityId);
        Assert.Contains(Language.German.Id, entry.LanguageIds);
        Assert.Equal("GRP", entry.ReleaseGroup);
        Assert.Equal(("Alpha", 1), (entry.Indexer, entry.IndexerId));
        Assert.Equal(12 * SearchTestBed.Gb, entry.Size);
        Assert.Equal(grabbed.NzbPath, entry.Data["nzbPath"]);
        Assert.Equal(release.DownloadUrl, entry.Data["downloadUrl"]);
        Assert.Equal(_bed.Time.Now.UtcDateTime, entry.Date);

        Assert.Equal(Title, Assert.Single(_bed.Grabbed).Title);
        Assert.True(_bed.History.MovieHasEventSince(_bed.Dune.Id, HistoryEventType.Grabbed, _bed.Time.Now.AddHours(-1).UtcDateTime));
        Assert.False(_bed.History.MovieHasEventSince(_bed.Dune.Id, HistoryEventType.Grabbed, _bed.Time.Now.AddHours(1).UtcDateTime));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_file_and_grabs_rejected_releases_too()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release("Dune.2021.720p.WEB.H264-ENG", 3));
        var result = await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch);
        var release = Assert.Single(result.Decisions);
        Assert.False(release.Accepted);

        var grab = _bed.Grab();
        var first = await grab.GrabAsync(release.Release.IndexerId, release.Release.Guid);
        var second = await grab.GrabAsync(release.Release.IndexerId, release.Release.Guid);

        Assert.NotEqual(first.NzbPath, second.NzbPath);
        Assert.Equal(["Dune.2021.720p.WEB.H264-ENG (2).nzb", "Dune.2021.720p.WEB.H264-ENG.nzb"], _bed.NzbFiles());
        Assert.Equal("false", _bed.History.Page(1, 10).Records[0].Data["accepted"]);
    }

    [Fact]
    public async Task Indexer_errors_are_recorded_and_readable()
    {
        _bed.AddIndexer(1, "Alpha");
        _bed.Client.Returns(1, _bed.Release(Title));
        var release = (await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch)).Decisions[0].Release;

        _bed.Client.Nzb = (_, _) => throw new IndexerException("Request limit reached", 500, TimeSpan.FromHours(1));
        var error = await Assert.ThrowsAsync<IndexerException>(() => _bed.Grab().GrabAsync(release.IndexerId, release.Guid));
        Assert.Equal("Alpha did not deliver the NZB: Request limit reached", error.Message);
        Assert.Equal(500, error.Code);

        _bed.Client.Nzb = (_, _) => Encoding.UTF8.GetBytes("<html><body>Please log in</body></html>");
        error = await Assert.ThrowsAsync<IndexerException>(() => _bed.Grab().GrabAsync(release.IndexerId, release.Guid));
        Assert.StartsWith("Alpha did not return an NZB file", error.Message, StringComparison.Ordinal);

        Assert.Equal(2, _bed.Indexers.Failures.Count);
        Assert.Empty(_bed.NzbFiles());
        Assert.Equal(0, _bed.History.Page(1, 10).Total);
    }

    [Fact]
    public async Task Unknown_or_expired_release_is_not_found()
    {
        _bed.AddIndexer(1, "Alpha");
        var error = await Assert.ThrowsAsync<LibraryException>(() => _bed.Grab().GrabAsync(1, "nope"));
        Assert.Equal(LibraryError.NotFound, error.Error);

        _bed.Client.Returns(1, _bed.Release(Title));
        var release = (await _bed.Search().SearchMovieAsync(_bed.Dune.Id, IndexerUse.InteractiveSearch)).Decisions[0].Release;
        _bed.Time.Now += TimeSpan.FromMinutes(31);
        await Assert.ThrowsAsync<LibraryException>(() => _bed.Grab().GrabAsync(release.IndexerId, release.Guid));
    }

    [Fact]
    public void Nzb_folder_is_validated_and_defaults_to_the_data_folder()
    {
        Assert.Equal(Path.Combine(_bed.DataDirectory, "downloads", "nzb"), _bed.Downloads.NzbFolder);

        Assert.Throws<ValidationFailedException>(() => _bed.Downloads.SetNzbFolder("relative/nzb"));

        var custom = Path.Combine(_bed.DataDirectory, "watch");
        Assert.Equal(custom, _bed.Downloads.SetNzbFolder(custom + Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(custom));
        Assert.Equal(custom, _bed.Downloads.NzbFolder);

        _bed.Downloads.SetNzbFolder("");
        Assert.Equal(_bed.Downloads.DefaultNzbFolder, _bed.Downloads.NzbFolder);
    }

    [Theory]
    [InlineData("Dune.2021.German.DL.1080p-GRP", "Dune.2021.German.DL.1080p-GRP")]
    [InlineData("Movie: Part 1 / 2 <Remux>?*|\"", "Movie_ Part 1 _ 2 _Remux_____")]
    [InlineData("  Name ending with dots...  ", "Name ending with dots")]
    [InlineData("...", "release")]
    [InlineData("a\tb", "a_b")]
    public void Safe_file_names(string title, string expected) => Assert.Equal(expected, NzbFileNames.SafeName(title));

    [Fact]
    public void Long_titles_are_shortened() => Assert.Equal(180, NzbFileNames.SafeName(new string('x', 400)).Length);

    public void Dispose() => _bed.Dispose();
}
