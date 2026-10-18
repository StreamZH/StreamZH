// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Import;
using StreamZH.Library.MediaFiles;
using StreamZH.Library.PostProcessing;
using StreamZH.Library.Search;
using StreamZH.Tests.Library.Import;
using StreamZH.Tests.PostProcessing;
using static StreamZH.Tests.CustomFormats.CustomFormatTestData;

namespace StreamZH.Tests.Library.MediaFiles;

/// <summary>
/// Analysing files with a stand-in ffprobe: details rows for library files, file languages and resolution from the media
/// info, the import (media info, languages, "Not a custom format upgrade").
/// </summary>
public sealed class MediaInfoServiceTests : IDisposable
{
    private const string FakeFfprobe = "/opt/ffmpeg/bin/ffprobe";

    private readonly ImportTestBed _bed = new();
    private string _fixture = "x264-ac3-eac3.json";

    public MediaInfoServiceTests()
    {
        Runner = new FakeToolRunner((file, arguments) =>
        {
            Assert.Equal(FakeFfprobe, file);
            var frames = arguments.Contains("-show_frames");
            var json = frames ? FfprobeParserTests.Fixture("dv-hdr10-truehd-atmos.frames.json") : FfprobeParserTests.Fixture(_fixture);
            return new ToolProcessResult(0, json);
        });
        var environment = new ToolEnvironment("/opt/streamzh", "/usr/bin:/opt/ffmpeg/bin", IsWindows: false, [])
        {
            FileExists = path => path == FakeFfprobe,
        };
        var tools = new PostProcessTools(new FixedToolSettings(), Runner, environment);
        Settings = new MediaInfoSettings(_bed.Bed.Config);
        Service = new MediaInfoService(tools, Runner, _bed.Details, _bed.Bed.Movies, _bed.Bed.Series, Settings, NullLogger<MediaInfoService>.Instance, _bed.Bed.Time);
    }

    private FakeToolRunner Runner { get; }

    private MediaInfoSettings Settings { get; }

    private MediaInfoService Service { get; }

    private string MovieFolder => _bed.Dune.FolderPath;

    private Movie WithFile(string name, long mb = 30)
    {
        ImportTestBed.WriteFile(Path.Combine(MovieFolder, name), mb);
        var movie = _bed.Dune;
        movie.File = new MediaFile(name, mb * ImportTestBed.Mb, DateTime.UtcNow);
        _bed.Bed.Movies.Update(movie);
        return movie;
    }

    [Fact]
    public async Task Probe_runs_ffprobe_and_reads_the_first_frame_only_for_hdr()
    {
        var path = Path.Combine(MovieFolder, "x.mkv");
        ImportTestBed.WriteFile(path, 1);

        var info = await Service.ProbeAsync(path, FfprobeParserTests.X264Release, CancellationToken.None);
        Assert.Equal("x264", info.Video!.Codec);
        Assert.Equal(ImportTestBed.Mb, info.FileSize);
        var call = Assert.Single(Runner.Calls);
        Assert.Equal(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-probesize", "50M", path], call.Arguments);

        _fixture = "dv-hdr10-truehd-atmos.json";
        var hdr = await Service.ProbeAsync(path, null, CancellationToken.None);
        Assert.Equal(MediaHdrType.DolbyVisionHdr10, hdr.Video!.Hdr);
        Assert.Equal(3, Runner.Calls.Count);
        Assert.Contains("-show_frames", Runner.Calls[2].Arguments);
        Assert.Contains("%+#1", Runner.Calls[2].Arguments);
    }

    [Fact]
    public async Task A_library_file_without_details_gets_a_row_with_languages_and_resolution_from_the_video()
    {
        // An old German file, named without language, source or resolution.
        var movie = WithFile("Dune (2021).mkv");

        var analysed = await Service.AnalyzeMovieAsync(movie.Id, force: false, CancellationToken.None);

        Assert.Equal(1, analysed);
        var details = _bed.Details.Find(Path.Combine(MovieFolder, "Dune (2021).mkv"))!;
        Assert.NotNull(details.MediaInfo);
        Assert.Equal(_bed.Bed.Time.Now.UtcDateTime, details.MediaInfoUpdated);
        Assert.Empty(details.LanguageIds); // the name has none …
        Assert.Equal([Language.German, Language.English], details.Languages); // … so the audio tracks count
        Assert.Equal(Quality.Hdtv1080p.Id, details.QualityId); // no source in the name: like Radarr, HDTV at the video's resolution

        // The decision engine sees a German (dual language) 1080p file.
        var target = new DecisionTargetFactory(_bed.Bed.Profiles, null, _bed.Details, _bed.Settings).ForMovie(_bed.Dune);
        Assert.Equal([Language.German, Language.English], target.ExistingLanguages);
        Assert.Equal(Quality.Hdtv1080p, target.ExistingQuality!.Quality);
        var profile = _bed.Bed.Profiles.Find(QualityDefaults.DefaultProfileId)!;
        Assert.True(LanguageRules.IsDualLanguage(target.ExistingLanguages, profile, Language.English));

        // Nothing changed: the next scan does not run ffprobe again.
        Assert.Equal(0, await Service.AnalyzeMovieAsync(movie.Id, force: false, CancellationToken.None));
        Assert.Single(Runner.Calls);
        Assert.Equal(1, await Service.AnalyzeMovieAsync(movie.Id, force: true, CancellationToken.None));
    }

    [Fact]
    public async Task Release_facts_win_over_media_info_and_the_resolution_only_fills_a_gap()
    {
        var path = Path.Combine(MovieFolder, "Dune (2021) WEBDL.mkv");
        WithFile("Dune (2021) WEBDL.mkv");
        _bed.Details.Save(new MediaFileDetails(
            path, Quality.WebDl480p.Id, 1, 0, false, [Language.English.Id], "GRP", "Dune.2021.WEB-DL.x264-GRP", null, "x264", null, DateTime.UtcNow));

        await Service.AnalyzeMovieAsync(_bed.Dune.Id, force: false, CancellationToken.None);

        var details = _bed.Details.Find(path)!;
        Assert.Equal(Quality.WebDl1080p.Id, details.QualityId); // WEB-DL from the release, 1080p from the video
        Assert.Equal([Language.English], details.Languages); // the release name said English
        Assert.Equal("Dune.2021.WEB-DL.x264-GRP", details.SceneName);

        // A release name with a resolution is never overruled.
        var named = Path.Combine(MovieFolder, "named.mkv");
        _bed.Details.Save(new MediaFileDetails(named, Quality.WebDl720p.Id, 1, 0, false, [], null, "Dune.2021.720p.WEB-DL-GRP", null, null, null, DateTime.UtcNow));
        ImportTestBed.WriteFile(named, 1);
        await Service.AnalyzeAsync(named, MovieFolder, force: true, CancellationToken.None);
        Assert.Equal(Quality.WebDl720p.Id, _bed.Details.Find(named)!.QualityId);
    }

    [Fact]
    public void A_changed_or_outdated_analysis_is_redone()
    {
        var path = Path.Combine(MovieFolder, "a.mkv");
        ImportTestBed.WriteFile(path, 2);
        var file = new FileInfo(path);
        var info = FfprobeParserTests.X264() with { FileSize = file.Length };
        var fresh = new MediaFileDetails(path, 0, 1, 0, false, [], null, null, null, null, null, DateTime.UtcNow, info, DateTime.UtcNow.AddMinutes(5));

        Assert.True(MediaInfoService.NeedsAnalysis(null, file));
        Assert.False(MediaInfoService.NeedsAnalysis(fresh, file));
        Assert.True(MediaInfoService.NeedsAnalysis(fresh with { MediaInfo = null }, file));
        Assert.True(MediaInfoService.NeedsAnalysis(fresh with { MediaInfo = info with { FileSize = 1 } }, file));
        Assert.True(MediaInfoService.NeedsAnalysis(fresh with { MediaInfo = info with { Revision = 0 } }, file));
        Assert.True(MediaInfoService.NeedsAnalysis(fresh with { MediaInfoUpdated = file.LastWriteTimeUtc.AddHours(-1) }, file));
    }

    [Fact]
    public async Task Without_ffprobe_or_with_analysing_off_nothing_runs()
    {
        WithFile("Dune (2021).mkv");
        Settings.Update(analyze: false, refreshIntervalDays: 0);

        Assert.False(Service.Enabled);
        Assert.Equal(0, await Service.AnalyzeLibraryAsync(force: true, CancellationToken.None));
        Assert.Empty(Runner.Calls);
        Assert.Null(Settings.RefreshInterval);

        Settings.Update(analyze: true, refreshIntervalDays: 7);
        Assert.Equal(TimeSpan.FromDays(7), new MediaInfoRefreshTask(Settings).Interval);
        Assert.Equal(1, await Service.AnalyzeLibraryAsync(force: false, CancellationToken.None));
    }

    [Fact]
    public async Task Ffprobe_errors_are_reported_and_nothing_is_stored()
    {
        var broken = new FakeToolRunner((_, _) => new ToolProcessResult(1, "x.mkv: Invalid data found when processing input"));
        var environment = new ToolEnvironment("/opt/streamzh", "/opt/ffmpeg/bin", IsWindows: false, []) { FileExists = path => path == FakeFfprobe };
        var service = new MediaInfoService(
            new PostProcessTools(new FixedToolSettings(), broken, environment), broken, _bed.Details, _bed.Bed.Movies, _bed.Bed.Series, Settings,
            NullLogger<MediaInfoService>.Instance);
        var movie = WithFile("Dune (2021).mkv");

        var result = await service.AnalyzeAsync(Path.Combine(MovieFolder, movie.File!.RelativePath), MovieFolder, force: true, CancellationToken.None);

        Assert.Equal("ffprobe could not read Dune (2021).mkv: x.mkv: Invalid data found when processing input", result.Problem);
        Assert.Null(_bed.Details.Find(Path.Combine(MovieFolder, "Dune (2021).mkv")));
    }

    // ------------------------------------------------------------------ import

    private DownloadImportService Import(CustomFormatRepository? formats = null) => new(
        _bed.Settings, _bed.Bed.Downloads, _bed.Bed.Movies, _bed.Bed.Series, _bed.EpisodeFiles, _bed.Details, _bed.Imports, _bed.Bed.History, _bed.Bed.Profiles,
        _bed.Files, _bed.Bed.Events, NullLogger<DownloadImportService>.Instance, _bed.Queue, null, null, _bed.Bed.Time, formats, mediaInfo: Service);

    [Fact]
    public async Task Import_stores_the_media_info_and_takes_languages_and_resolution_from_it()
    {
        // The release name tells neither the language nor the resolution.
        var item = _bed.Completed(1, "Dune.2021.WEB-DL.x264-GRP", _bed.MovieLink, ("dune.mkv", 60));

        var result = await Import().ImportAsync(item.Id);

        Assert.Equal(ImportState.Imported, result.State);
        var path = Assert.Single(result.Files);
        Assert.EndsWith("WEBDL-1080p.mkv", path, StringComparison.Ordinal);
        var details = _bed.Details.Find(path)!;
        Assert.Equal(Quality.WebDl1080p.Id, details.QualityId);
        Assert.Equal([Language.German.Id, Language.English.Id], details.LanguageIds);
        Assert.Equal("x264", details.MediaInfo!.Video!.Codec);
        Assert.NotNull(details.MediaInfoUpdated);
        Assert.False(MediaInfoService.NeedsAnalysis(details, new FileInfo(path)));
    }

    [Fact]
    public async Task Import_names_files_with_media_info_tokens()
    {
        _bed.Configure(v => v with { StandardMovieFormat = "{Movie Title} ({Release Year}) {[Quality Full]}{[Mediainfo AudioCodec}{ Mediainfo AudioChannels]}{MediaInfo AudioLanguages}{[MediaInfo VideoCodec]}" });
        var item = _bed.Completed(1, "Dune.2021.German.DL.1080p.BluRay.x264-GRP", _bed.MovieLink, ("dune.mkv", 60));

        var result = await Import().ImportAsync(item.Id);

        Assert.Equal("Dune (2021) [Bluray-1080p][AC3 5.1][DE+EN][x264].mkv", Path.GetFileName(Assert.Single(result.Files)));
    }

    [Fact]
    public async Task Same_quality_with_a_lower_custom_format_score_is_not_imported()
    {
        var formats = new CustomFormatRepository(_bed.Bed.Database);
        var germanDl = formats.Add(Movie("German DL", 0, Title(@"German\.DL")));
        var profile = _bed.Bed.Profiles.Find(QualityDefaults.DefaultProfileId)!;
        _bed.Bed.Profiles.Update(profile with { FormatItems = [new ProfileFormatItem(germanDl, 100)] });
        var existing = Path.Combine(MovieFolder, "Dune.2021.German.DL.1080p.BluRay.x264-GOOD.mkv");
        WithFile(Path.GetFileName(existing));

        var item = _bed.Completed(1, "Dune.2021.German.1080p.BluRay.x264-OTHER", _bed.MovieLink, ("dune.mkv", 60));
        var result = await Import(formats).ImportAsync(item.Id);

        Assert.Equal(ImportState.Failed, result.State);
        Assert.Equal("Not a custom format upgrade: the file in the library scores +100 (German DL), this one 0", result.Message);
        Assert.True(File.Exists(existing));
        Assert.True(File.Exists(Path.Combine(item.OutputPath!, "dune.mkv")));

        // The same score (or a higher one) is imported, like Radarr.
        var better = _bed.Completed(2, "Dune.2021.German.DL.1080p.BluRay.x264-OTHER", _bed.MovieLink, ("dune.mkv", 60));
        Assert.Equal(ImportState.Imported, (await Import(formats).ImportAsync(better.Id)).State);
    }

    public void Dispose() => _bed.Dispose();
}
