// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Renamer;
using StreamZH.Metadata.Tmdb;
using StreamZH.Tests.Library.Import;

namespace StreamZH.Tests.Library.Renamer;

/// <summary>TMDB with "Dark" (episode titles per season) and "Arrival"; counts the calls.</summary>
public sealed class RenamerFakeTmdb : ITmdbClient
{
    public static readonly TmdbSeries Dark = new(70523, 334824, "tt5753856", "Dark", "Dark", 2017, null, "Ended", "Netflix", ["Drama"], null, null, [1, 2]);
    public static readonly TmdbMovie Arrival = new(329865, "tt2543164", "Arrival", "Arrival", 2016, null, 116, "Released", ["Drama"], null, null, "en");

    private static readonly string[] Season1 = ["Secrets", "Lies", "Past and Present", "Double Lives", "Truths"];
    private static readonly string[] Season2 = ["Beginnings and Endings", "Dark Matter"];

    public int Calls { get; private set; }

    public bool IsConfigured { get; set; } = true;

    public Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(string query, int? year, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TmdbSearchResult>>(
            "Arrival".Contains(query, StringComparison.OrdinalIgnoreCase) ? [new TmdbSearchResult(Arrival.TmdbId, "Arrival", "Arrival", 2016, null, null)] : []);
    }

    public Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(string query, int? year, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TmdbSearchResult>>(
            query.Equals("dark", StringComparison.OrdinalIgnoreCase) ? [new TmdbSearchResult(Dark.TmdbId, "Dark", "Dark", 2017, null, null)] : []);
    }

    public Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return tmdbId == Arrival.TmdbId ? Task.FromResult(Arrival) : throw new TmdbException("Not found", 404);
    }

    public Task<TmdbSeries> GetSeriesAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return tmdbId == Dark.TmdbId ? Task.FromResult(Dark) : throw new TmdbException("Not found", 404);
    }

    public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        Calls++;
        var titles = seasonNumber == 1 ? Season1 : seasonNumber == 2 ? Season2 : [];
        return Task.FromResult<IReadOnlyList<TmdbEpisode>>([.. titles.Select((t, i) => new TmdbEpisode(seasonNumber, i + 1, t, $"2017-12-0{i + 1}", null))]);
    }

    public Task TestAsync(string? token = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RenamerServiceTests : IDisposable
{
    private const string EpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title}";

    private readonly ImportTestBed _bed = new();
    private readonly RenamerFakeTmdb _tmdb = new();
    private readonly JobQueue _jobs;
    private readonly RenamerTmdbCache _cache;

    public RenamerServiceTests()
    {
        _jobs = new JobQueue(_bed.Bed.Events);
        _cache = new RenamerTmdbCache(_bed.Bed.Time);
        Directory.CreateDirectory(Work);
    }

    private string Work => Path.Combine(_bed.Bed.DataDirectory, "work");

    private RenamerService Service(bool caseInsensitive = false) =>
        new(
            _bed.Settings,
            new RenamerCatalog(_bed.Bed.Movies, _bed.Bed.Series, _bed.Details, _cache, _tmdb),
            new RenamerRepository(_bed.Bed.Database),
            new RootFolderRepository(_bed.Bed.Database),
            _bed.Bed.Movies,
            _bed.Bed.Series,
            _bed.EpisodeFiles,
            _bed.Details,
            _bed.Bed.History,
            _bed.Files,
            new RenamerProgress(_bed.Bed.Events, _bed.Bed.Time),
            _jobs,
            _bed.Bed.Events,
            new AppPaths(Path.Combine(_bed.Bed.DataDirectory, "appdata")),
            NullLogger<RenamerService>.Instance,
            _bed.Bed.Time)
        {
            CaseInsensitiveFileSystem = caseInsensitive,
        };

    private static RenamerRequest Request(string folder, params RenameRule[] rules) => new()
    {
        Source = new RenamerSource { Path = folder, Subfolders = true, Depth = 3 },
        Filter = new RenamerFilter { Extensions = ExtensionPreset.All },
        Rules = rules,
    };

    private string Touch(string relative, long mb = 0)
    {
        var path = Path.Combine(Work, relative);
        ImportTestBed.WriteFile(path, mb);
        return path;
    }

    private static string[] Names(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    private async Task<RenamerRun> ApplyAsync(RenamerService service, RenamerRequest request)
    {
        var id = await service.QueueApplyAsync(request);
        await service.ExecuteAsync(id);
        return service.Run(id).Run;
    }

    // ------------------------------------------------------------------ scan and filter

    [Fact]
    public void Scan_filters_by_extension_glob_regex_size_and_samples()
    {
        Touch("Show/Season 1/Show.S01E01.mkv", 2);
        Touch("Show/Season 1/Show.S01E01.de.srt");
        Touch("Show/Season 1/Show.S01E02.mkv", 2);
        Touch("Show/Season 1/show.s01e02.sample.mkv");
        Touch("Show/Sample/clip.mkv");
        Touch("Show/.hidden/secret.mkv");
        Touch("Show/cover.jpg");
        var service = Service();

        var video = service.Scan(new RenamerSource { Path = Work, Subfolders = true, Depth = 5 }, new RenamerFilter());
        Assert.Equal(["Show/Season 1/Show.S01E01.mkv", "Show/Season 1/Show.S01E02.mkv"], video.Entries.Select(e => e.Key));

        var all = service.Scan(new RenamerSource { Path = Work, Subfolders = true }, new RenamerFilter { Extensions = ExtensionPreset.All, HideSamples = false });
        Assert.Equal(6, all.Matched);
        Assert.DoesNotContain(all.Entries, e => e.Key.Contains(".hidden", StringComparison.Ordinal));

        var include = service.Scan(new RenamerSource { Path = Work, Subfolders = true }, new RenamerFilter { Extensions = ExtensionPreset.VideoAndSubtitles, Include = "*E01*" });
        Assert.Equal(["Show/Season 1/Show.S01E01.de.srt", "Show/Season 1/Show.S01E01.mkv"], include.Entries.Select(e => e.Key).Order(StringComparer.Ordinal));

        var regex = service.Scan(new RenamerSource { Path = Work, Subfolders = true }, new RenamerFilter { Extensions = ExtensionPreset.All, Exclude = "/\\.(srt|jpg)$/" });
        Assert.Equal(2, regex.Matched);

        var size = service.Scan(new RenamerSource { Path = Work, Subfolders = true }, new RenamerFilter { MinSizeMb = 1, MaxSizeMb = 3, HideSamples = false });
        Assert.Equal(2, size.Matched);

        var flat = service.Scan(new RenamerSource { Path = Work, Target = RenameTarget.FilesAndFolders }, new RenamerFilter { Extensions = ExtensionPreset.All });
        Assert.Equal(["Show"], flat.Entries.Select(e => e.Key));
        Assert.True(flat.Entries[0].IsFolder);
    }

    [Fact]
    public void Unsafe_sources_are_refused()
    {
        var service = Service();
        var errors = new[] { "/", "relative/path", Path.Combine(Work, ".git"), "/proc/self", Path.Combine(_bed.Bed.DataDirectory, "appdata", "logs"), Path.Combine(Work, "missing") }
            .Select(p =>
            {
                Directory.CreateDirectory(Path.Combine(_bed.Bed.DataDirectory, "appdata", "logs"));
                Directory.CreateDirectory(Path.Combine(Work, ".git"));
                return Assert.Throws<ValidationFailedException>(() => service.Scan(new RenamerSource { Path = p }, new RenamerFilter())).Errors["source.path"][0];
            })
            .ToList();
        Assert.Equal(
            [
                "Choose a folder, not a whole drive.",
                "Enter a full path.",
                "Hidden and system folders cannot be renamed in.",
                "Hidden and system folders cannot be renamed in.",
                "StreamZH's own data folder cannot be renamed in.",
                "This folder does not exist.",
            ],
            errors);
        var filter = Assert.Throws<ValidationFailedException>(() => service.Scan(new RenamerSource { Path = Work }, new RenamerFilter { Include = "/(/" }));
        Assert.Contains("filter.include", filter.Errors.Keys);
    }

    // ------------------------------------------------------------------ preview statuses

    [Fact]
    public async Task Preview_shows_unchanged_rename_and_every_kind_of_conflict()
    {
        Touch("a.mkv");
        Touch("b.mkv");
        Touch("c.mkv");
        Touch("keep.mkv");
        Touch("taken.mkv");
        Touch("Taken (1).mkv");
        var service = Service();
        var rules = new RenameRule[]
        {
            new ReplaceRule { Find = "^[ab]$", ReplaceWith = "same", UseRegex = true },
            new ReplaceRule { Find = "^c$", ReplaceWith = "taken", UseRegex = true },
            new ReplaceRule { Find = "^Taken \\(1\\)$", ReplaceWith = "x:y", UseRegex = true, CaseSensitive = true },
        };

        var preview = await service.PreviewAsync(Request(Work, rules));
        var rows = preview.Rows.ToDictionary(r => r.Key);
        Assert.Equal((RowStatus.Conflict, RowProblems.Duplicate), (rows["a.mkv"].Status, rows["a.mkv"].Problem));
        Assert.Equal((RowStatus.Conflict, RowProblems.Duplicate), (rows["b.mkv"].Status, rows["b.mkv"].Problem));
        Assert.Equal((RowStatus.Conflict, RowProblems.Exists), (rows["c.mkv"].Status, rows["c.mkv"].Problem));
        Assert.Equal((RowStatus.Conflict, RowProblems.Invalid), (rows["Taken (1).mkv"].Status, rows["Taken (1).mkv"].Problem));
        Assert.Equal(RowStatus.Unchanged, rows["keep.mkv"].Status);
        Assert.Equal(new PreviewCounts(6, 2, 0, 4, 0, 0), preview.Counts);

        var tooLong = await service.PreviewAsync(Request(Work, new InsertRule { Text = new string('x', 260) }));
        Assert.All(tooLong.Rows, r => Assert.Equal(RowProblems.TooLong, r.Problem));

        // Excluded rows are left out of the run and their names stay taken.
        var excluded = await service.PreviewAsync(Request(Work, new InsertRule { Text = "new " }) with { Excluded = ["a.mkv"] });
        Assert.Equal(RowStatus.Excluded, excluded.Rows.Single(r => r.Key == "a.mkv").Status);
        Assert.Equal(5, excluded.Counts.Rename);
    }

    [Fact]
    public async Task Invalid_rules_are_reported_and_skipped_and_block_the_run()
    {
        Touch("a.mkv");
        var service = Service();
        var request = Request(Work, new ReplaceRule { Find = "(", UseRegex = true }, new InsertRule { Text = "x" });

        var preview = await service.PreviewAsync(request);
        Assert.Equal("A ( is not closed (at character 1).", preview.RuleErrors[0]);
        Assert.Equal("xa.mkv", preview.Rows.Single().NewName);

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => service.QueueApplyAsync(request));
        Assert.Contains("rules[0]", error.Errors.Keys);
        await Assert.ThrowsAsync<LibraryException>(() => service.QueueApplyAsync(Request(Work)));
    }

    [Fact]
    public async Task A_regex_that_times_out_is_a_row_error_not_a_crash()
    {
        Touch(new string('a', 40) + "!.mkv");
        var preview = await Service().PreviewAsync(Request(Work, new ReplaceRule { Find = "^(a+)+$", ReplaceWith = "x", UseRegex = true }));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(RowStatus.Error, row.Status);
        Assert.Contains("took too long", row.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ apply and undo

    [Fact]
    public async Task Rename_runs_as_a_job_is_logged_and_can_be_undone()
    {
        Touch("Show/the.show.s01e01.mkv");
        Touch("Show/the.show.s01e02.mkv");
        var service = Service();
        var request = Request(Work, new RemoveRule { Mode = RemoveMode.Separators }, new CaseRule());

        var id = await service.QueueApplyAsync(request);
        Assert.Equal(1, _jobs.Status.Queued);
        Assert.Equal(RunState.Queued, service.Run(id).Run.State);
        await service.ExecuteAsync(id);

        Assert.Equal(["Show", "Show/The Show S01E01.mkv", "Show/The Show S01E02.mkv"], Names(Work));
        var run = service.Run(id);
        Assert.Equal((RunState.Completed, 2, 0), (run.Run.State, run.Run.Done, run.Run.Failed));
        Assert.Equal("Dots to spaces · Title Case", run.Run.Summary);
        Assert.All(run.Items, i => Assert.Equal(RunItemState.Done, i.State));
        Assert.Equal(2, service.History().Single().Rules.Count);

        service.QueueUndo(id);
        Assert.Equal(RunState.Undoing, service.Run(id).Run.State);
        await service.UndoAsync(id);
        Assert.Equal(["Show", "Show/the.show.s01e01.mkv", "Show/the.show.s01e02.mkv"], Names(Work));
        Assert.Equal(RunState.Undone, service.Run(id).Run.State);
        Assert.NotNull(service.Run(id).Run.UndoneAt);
        Assert.Throws<LibraryException>(() => service.QueueUndo(id));
    }

    [Fact]
    public async Task Never_overwrites_a_file_that_appeared_after_the_preview()
    {
        Touch("a.mkv");
        var service = Service();
        var id = await service.QueueApplyAsync(Request(Work, new InsertRule { Text = "b", Where = InsertPosition.Suffix }));
        File.WriteAllText(Path.Combine(Work, "ab.mkv"), "someone else");

        await service.ExecuteAsync(id);
        var run = service.Run(id);
        Assert.Equal(RunState.Failed, run.Run.State);
        Assert.Equal("A file or folder with the new name already exists.", run.Items.Single().Error);
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(Work, "ab.mkv")));
        Assert.True(File.Exists(Path.Combine(Work, "a.mkv")));
    }

    [Fact]
    public async Task Swaps_and_chains_go_through_temporary_names()
    {
        File.WriteAllText(Path.Combine(Work, "1.mkv"), "one");
        File.WriteAllText(Path.Combine(Work, "2.mkv"), "two");
        var service = Service();

        // 1 → 2 and 2 → 1 at the same time.
        var swap = Request(Work, new ReplaceRule { Find = "^1$", ReplaceWith = "x", UseRegex = true }, new ReplaceRule { Find = "^2$", ReplaceWith = "1", UseRegex = true },
            new ReplaceRule { Find = "^x$", ReplaceWith = "2", UseRegex = true });
        var preview = await service.PreviewAsync(swap);
        Assert.All(preview.Rows, r => Assert.Equal(RowStatus.Rename, r.Status));
        await ApplyAsync(service, swap);
        Assert.Equal("two", File.ReadAllText(Path.Combine(Work, "1.mkv")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Work, "2.mkv")));
        Assert.Equal(["1.mkv", "2.mkv"], Names(Work));
    }

    [Fact]
    public async Task Case_only_renames_take_two_steps_on_case_insensitive_file_systems()
    {
        File.WriteAllText(Path.Combine(Work, "the show.mkv"), "x");
        var service = Service(caseInsensitive: true);
        var request = Request(Work, new CaseRule(), new ExtensionRule { Mode = ExtensionMode.Upper });

        var row = Assert.Single((await service.PreviewAsync(request)).Rows);
        Assert.Equal((RowStatus.Rename, "The Show.MKV"), (row.Status, row.NewName));
        var run = await ApplyAsync(service, request);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(["The Show.MKV"], Names(Work));

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(["the show.mkv"], Names(Work));
    }

    [Fact]
    public async Task Folders_are_renamed_after_their_files_and_undone_before_them()
    {
        Touch("show/season.1/a.mkv");
        var service = Service();
        var request = Request(Work, new RemoveRule { Mode = RemoveMode.Separators }, new CaseRule()) with
        {
            Source = new RenamerSource { Path = Work, Subfolders = true, Depth = 3, Target = RenameTarget.FilesAndFolders },
        };

        var preview = await service.PreviewAsync(request);
        Assert.Equal(["show", "show/season.1", "show/season.1/a.mkv"], preview.Rows.Select(r => r.Key));
        var run = await ApplyAsync(service, request);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(["Show", "Show/Season 1", "Show/Season 1/A.mkv"], Names(Work));

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(["show", "show/season.1", "show/season.1/a.mkv"], Names(Work));
    }

    [Fact]
    public async Task Library_folders_cannot_be_renamed_here()
    {
        Directory.CreateDirectory(_bed.Show.FolderPath);
        var service = Service();
        var request = Request(_bed.Media, new InsertRule { Text = "x" }) with
        {
            Source = new RenamerSource { Path = _bed.Media, Target = RenameTarget.Folders },
        };

        var row = Assert.Single((await service.PreviewAsync(request)).Rows);
        Assert.Equal((RowStatus.Conflict, RowProblems.LibraryFolder), (row.Status, row.Problem));
    }

    [Fact]
    public async Task Subtitles_follow_their_video()
    {
        Touch("Show.S01E01.mkv");
        Touch("Show.S01E01.de.srt");
        Touch("Show.S01E01.en.forced.srt");
        Touch("Other.srt");
        var service = Service();
        var request = Request(Work, new ReplaceRule { Find = "Show", ReplaceWith = "Series" }) with { Filter = new RenamerFilter() };

        var preview = await service.PreviewAsync(request);
        Assert.Equal(["Show.S01E01.mkv", "Show.S01E01.de.srt", "Show.S01E01.en.forced.srt"], preview.Rows.Select(r => r.Key));
        Assert.Equal("Show.S01E01.mkv", preview.Rows[1].CompanionOf);
        Assert.Equal("Series.S01E01.de.srt", preview.Rows[1].NewName);

        var excluded = await service.PreviewAsync(request with { Excluded = ["Show.S01E01.mkv"] });
        Assert.All(excluded.Rows, r => Assert.Equal(RowStatus.Excluded, r.Status));

        await ApplyAsync(service, request);
        Assert.Equal(["Other.srt", "Series.S01E01.de.srt", "Series.S01E01.en.forced.srt", "Series.S01E01.mkv"], Names(Work));
    }

    // ------------------------------------------------------------------ media rule

    [Fact]
    public async Task Media_rule_names_garbage_episode_files_with_titles_from_tmdb()
    {
        Touch("Dark/Season 1/dark s01e01 xvid garbage.avi");
        Touch("Dark/Season 1/DARK.1x02.mkv");
        Touch("Dark/Season 1/03.mkv");
        Touch("Dark/Season 1/S01E04.mkv");
        Touch("Dark/Season 2/dark.s02e02.mkv");
        var service = Service();
        var request = Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat });

        var preview = await service.PreviewAsync(request);
        Assert.Equal(
            [
                "Dark - S01E01 - Secrets.avi",
                "Dark - S01E02 - Lies.mkv",
                "Dark - S01E03 - Past and Present.mkv",
                "Dark - S01E04 - Double Lives.mkv",
                "Dark - S02E02 - Dark Matter.mkv",
            ],
            preview.Rows.Select(r => r.NewName).Order(StringComparer.Ordinal));
        var media = preview.Rows.Single(r => r.Key.EndsWith("garbage.avi", StringComparison.Ordinal)).Media!;
        Assert.Equal(("episode", "Dark", 1, "Secrets", "tmdb"), (media.Kind, media.Title, media.Season, media.EpisodeTitle, media.Source));

        // The TMDB answers are cached: a second preview costs no calls.
        var calls = _tmdb.Calls;
        await service.PreviewAsync(request);
        await Service().PreviewAsync(request);
        Assert.Equal(calls, _tmdb.Calls);
    }

    [Fact]
    public async Task Media_rule_season_override_and_episode_offset()
    {
        Touch("Dark/Dark - Folge 13.mkv");
        Touch("Dark/Dark - Folge 14.mkv");
        var service = Service();

        var withoutSeason = await service.PreviewAsync(Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat }));
        Assert.All(withoutSeason.Rows, r => Assert.Equal(RowStatus.Error, r.Status));

        var preview = await service.PreviewAsync(Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat, Season = 2, EpisodeOffset = -12 }));
        Assert.Equal(["Dark - S02E01 - Beginnings and Endings.mkv", "Dark - S02E02 - Dark Matter.mkv"], preview.Rows.Select(r => r.NewName));
    }

    [Fact]
    public async Task Media_rule_without_a_match_uses_the_name_and_notes_missing_episodes()
    {
        Touch("Unknown.Show.S03E07.720p.HDTV.x264-GRP.mkv");
        Touch("Dark.S01E09.mkv");
        _tmdb.IsConfigured = false;
        var preview = await Service().PreviewAsync(Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat }));
        Assert.Equal(["Dark - S01E09.mkv", "Unknown Show - S03E07.mkv"], preview.Rows.Select(r => r.NewName).Order(StringComparer.Ordinal));
        Assert.All(preview.Rows, r => Assert.Equal("name", r.Media!.Source));

        _tmdb.IsConfigured = true;
        var matched = await Service().PreviewAsync(Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat, Match = MediaMatch.Tmdb }));
        Assert.Equal("S01E09 not found", matched.Rows.Single(r => r.Key == "Dark.S01E09.mkv").Media!.Note);
    }

    [Fact]
    public async Task Media_rule_names_movies_with_the_settings_format()
    {
        Touch("downloads/arrival.2016.1080p.bluray.x264-grp.mkv");
        var preview = await Service().PreviewAsync(Request(Work, new MediaRule()));
        var row = Assert.Single(preview.Rows);
        Assert.Equal("Arrival (2016) Bluray-1080p.mkv", row.NewName);
        Assert.Equal(("movie", "tmdb"), (row.Media!.Kind, row.Media.Source));

        var custom = await Service().PreviewAsync(Request(Work, new MediaRule { MovieFormat = "{Movie.Title}.{Release.Year}-{Release Group}" }));
        Assert.Equal("Arrival.2016-grp.mkv", custom.Rows.Single().NewName);
        Assert.StartsWith("Movie format:", RuleEngine.Check(new MediaRule { MovieFormat = "{Release Year}" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Media_rule_moves_episodes_into_season_folders_and_undo_removes_them()
    {
        Touch("Dark/dark.s01e01.mkv");
        Touch("Dark/dark.s02e01.mkv");
        var service = Service();
        var request = Request(Work, new MediaRule { EpisodeFormat = EpisodeFormat }) with { Options = new RenamerOptions { FolderFormat = "Season {season:00}" } };

        var preview = await service.PreviewAsync(request);
        Assert.Equal(["Dark/Season 01", "Dark/Season 02"], preview.Rows.Select(r => r.NewFolder));
        var run = await ApplyAsync(service, request);
        Assert.Equal(
            ["Dark", "Dark/Season 01", "Dark/Season 01/Dark - S01E01 - Secrets.mkv", "Dark/Season 02", "Dark/Season 02/Dark - S02E01 - Beginnings and Endings.mkv"],
            Names(Work));
        Assert.Equal(2, service.Run(run.Id).Items.Count(i => i.Kind == RunItemKind.CreateFolder));

        // Already in the right folder: no extra level.
        Assert.All((await service.PreviewAsync(request)).Rows, r => Assert.Equal(RowStatus.Unchanged, r.Status));

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(["Dark", "Dark/dark.s01e01.mkv", "Dark/dark.s02e01.mkv"], Names(Work));

        Assert.Equal("Only one folder level; remove / and \\.", RenamerService.CheckFolderFormat("Season {season}/x"));
        Assert.Equal("Unknown token {Nope} here. See the token list.", RenamerService.CheckFolderFormat("{Nope}"));
        Assert.Null(RenamerService.CheckFolderFormat("{folder} extras"));
        var noMedia = await service.PreviewAsync(Request(Work, new InsertRule { Text = "x" }) with { Options = new RenamerOptions { FolderFormat = "Season {season}" } });
        Assert.All(noMedia.Rows, r => Assert.Equal(RowStatus.Error, r.Status));
    }

    private static RenamerRequest SeasonFolders(string folder) =>
        Request(folder, new MediaRule { EpisodeFormat = EpisodeFormat }) with { Options = new RenamerOptions { FolderFormat = "Season {season:00}" } };

    [Fact]
    public async Task Files_in_a_season_folder_move_to_the_sibling_season_folder_and_the_empty_one_is_removed()
    {
        Touch("Dark/Season 1/dark.s01e01.mkv");
        Touch("Dark/Season 1/dark.s01e02.mkv");
        Touch("Dark/Staffel 2/dark.s02e01.mkv");
        Directory.CreateDirectory(Path.Combine(Work, "Dark", "Extras"));
        var source = Path.Combine(Work, "Dark");
        var service = Service();
        var request = SeasonFolders(source);
        var before = Names(source);

        var preview = await service.PreviewAsync(request);
        Assert.Equal(["Season 01", "Season 01", "Season 02"], preview.Rows.Select(r => r.NewFolder));
        Assert.All(preview.Rows, r => Assert.Equal(RowStatus.Rename, r.Status));

        var run = await ApplyAsync(service, request);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(
            [
                "Extras",
                "Season 01",
                "Season 01/Dark - S01E01 - Secrets.mkv",
                "Season 01/Dark - S01E02 - Lies.mkv",
                "Season 02",
                "Season 02/Dark - S02E01 - Beginnings and Endings.mkv",
            ],
            Names(source));
        var items = service.Run(run.Id).Items;
        Assert.Equal(
            [Path.Combine(source, "Season 1"), Path.Combine(source, "Staffel 2")],
            items.Where(i => i.Kind == RunItemKind.RemoveFolder).Select(i => i.OldPath).Order(StringComparer.Ordinal));
        Assert.All(items.Where(i => i.Kind == RunItemKind.RemoveFolder), i => Assert.Equal((RunItemState.Done, true, ""), (i.State, i.IsFolder, i.NewPath)));
        Assert.Equal((3, 0), (run.Done, run.Failed));

        // Already in the right folder: nothing to do.
        Assert.All((await service.PreviewAsync(request)).Rows, r => Assert.Equal(RowStatus.Unchanged, r.Status));

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(before, Names(source));
        Assert.Equal(RunState.Undone, service.Run(run.Id).Run.State);
        Assert.All(service.Run(run.Id).Items, i => Assert.Equal(RunItemState.Undone, i.State));
    }

    [Fact]
    public async Task A_season_folder_chosen_as_the_source_gets_the_target_as_a_subfolder()
    {
        Touch("Dark/Season 1/dark.s01e01.mkv");
        var source = Path.Combine(Work, "Dark", "Season 1");
        var service = Service();
        var request = SeasonFolders(source);

        Assert.Equal("Season 01", (await service.PreviewAsync(request)).Rows.Single().NewFolder);
        var run = await ApplyAsync(service, request);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(["Season 01", "Season 01/Dark - S01E01 - Secrets.mkv"], Names(source));
        Assert.DoesNotContain(service.Run(run.Id).Items, i => i.Kind == RunItemKind.RemoveFolder);

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(["dark.s01e01.mkv"], Names(source));
    }

    [Fact]
    public async Task Files_in_other_folders_still_get_the_target_as_a_subfolder()
    {
        Touch("Downloads/dark.s01e01.mkv");
        var service = Service();
        var request = SeasonFolders(Work);

        Assert.Equal("Downloads/Season 01", (await service.PreviewAsync(request)).Rows.Single().NewFolder);
        var run = await ApplyAsync(service, request);
        Assert.Equal(["Downloads", "Downloads/Season 01", "Downloads/Season 01/Dark - S01E01 - Secrets.mkv"], Names(Work));
        Assert.DoesNotContain(service.Run(run.Id).Items, i => i.Kind == RunItemKind.RemoveFolder);
    }

    [Fact]
    public async Task A_season_folder_that_still_holds_files_is_kept()
    {
        Touch("Dark/Season 1/dark.s01e01.mkv");
        Touch("Dark/Season 1/dark.s01e02.mkv");
        var source = Path.Combine(Work, "Dark");
        var service = Service();
        var request = SeasonFolders(source) with { Excluded = ["Season 1/dark.s01e02.mkv"] };

        var run = await ApplyAsync(service, request);
        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal(
            ["Season 01", "Season 01/Dark - S01E01 - Secrets.mkv", "Season 1", "Season 1/dark.s01e02.mkv"],
            Names(source));
        Assert.DoesNotContain(service.Run(run.Id).Items, i => i.Kind == RunItemKind.RemoveFolder);

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(["Season 1", "Season 1/dark.s01e01.mkv", "Season 1/dark.s01e02.mkv"], Names(source));
    }

    [Fact]
    public async Task Library_files_keep_their_library_entry_and_details()
    {
        var show = _bed.Show;
        var oldRelative = Path.Combine("Season 1", "bb.s01e02.720p.hdtv.x264-grp.mkv");
        var oldPath = Path.Combine(show.FolderPath, oldRelative);
        ImportTestBed.WriteFile(oldPath, 1);
        ImportTestBed.WriteFile(Path.Combine(show.FolderPath, "Season 1", "bb.s01e02.720p.hdtv.x264-grp.en.srt"), 0);
        var episode = _bed.Bed.Episode(1, 2);
        _bed.EpisodeFiles.SetFile([episode.Id], new MediaFile(oldRelative, 1, DateTime.UtcNow));
        _bed.Details.Save(new MediaFileDetails(oldPath, StreamZH.Core.Quality.Quality.Hdtv720p.Id, 1, 0, false, [1], "GRP", "BB.S01E02", null, null, null, DateTime.UtcNow));
        var service = Service();
        var request = new RenamerRequest
        {
            Source = new RenamerSource { SeriesId = show.Id, Subfolders = true },
            Rules = [new MediaRule { EpisodeFormat = "{Series Title} - S{season:00}E{episode:00} - {Episode Title} {Quality Full}" }],
        };

        var row = Assert.Single((await service.PreviewAsync(request)).Rows, r => r.CompanionOf is null);
        Assert.Equal("Breaking Bad - S01E02 - Episode 2 HDTV-720p.mkv", row.NewName);
        Assert.Equal(show.Id, row.SeriesId);
        Assert.Equal("library", row.Media!.Source);

        var run = await ApplyAsync(service, request);
        Assert.Equal(2, run.Done);
        var newRelative = Path.Combine("Season 1", "Breaking Bad - S01E02 - Episode 2 HDTV-720p.mkv");
        Assert.Equal(newRelative, _bed.Bed.Episode(1, 2).File!.RelativePath);
        Assert.True(File.Exists(Path.Combine(show.FolderPath, "Season 1", "Breaking Bad - S01E02 - Episode 2 HDTV-720p.en.srt")));
        Assert.Equal("GRP", _bed.Details.Find(Path.Combine(show.FolderPath, newRelative))!.ReleaseGroup);
        Assert.Null(_bed.Details.Find(oldPath));
        var renamed = Assert.Single(_bed.Renamed);
        Assert.Equal((MediaType.Series, show.Id), (renamed.MediaType, renamed.SeriesId));
        Assert.Contains(_bed.Bed.History.Page(1, 10).Records, h => h.EventType == HistoryEventType.Renamed && h.SeriesId == show.Id);

        await service.UndoAsync(service.QueueUndo(run.Id));
        Assert.Equal(oldRelative, _bed.Bed.Episode(1, 2).File!.RelativePath);
        Assert.NotNull(_bed.Details.Find(oldPath));
        Assert.Equal(2, _bed.Renamed.Count);
    }

    [Fact]
    public async Task Renaming_a_season_folder_updates_the_episode_paths()
    {
        var show = _bed.Show;
        var oldRelative = Path.Combine("season.1", "Breaking Bad - S01E01.mkv");
        ImportTestBed.WriteFile(Path.Combine(show.FolderPath, oldRelative), 1);
        _bed.EpisodeFiles.SetFile([_bed.Bed.Episode(1, 1).Id], new MediaFile(oldRelative, 1, DateTime.UtcNow));
        var service = Service();
        var request = new RenamerRequest
        {
            Source = new RenamerSource { SeriesId = show.Id, Target = RenameTarget.Folders },
            Filter = new RenamerFilter { Extensions = ExtensionPreset.All },
            Rules = [new ReplaceRule { Find = "season.", ReplaceWith = "Season " }],
        };

        await ApplyAsync(service, request);
        Assert.Equal(Path.Combine("Season 1", "Breaking Bad - S01E01.mkv"), _bed.Bed.Episode(1, 1).File!.RelativePath);
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void Presets_are_saved_renamed_and_deleted()
    {
        var service = Service();
        var content = new RenamerPresetContent { Rules = [new MediaRule { Season = 2 }, new CaseRule { Language = "de" }], Options = new RenamerOptions { FolderFormat = "Season {season}" } };
        var preset = service.SavePreset(null, " Anime ", content);
        Assert.Equal("Anime", preset.Name);
        Assert.Equal(2, ((MediaRule)preset.Content.Rules[0]).Season);
        Assert.Equal("de", ((CaseRule)preset.Content.Rules[1]).Language);

        Assert.Throws<ValidationFailedException>(() => service.SavePreset(null, "anime", content));
        Assert.Throws<ValidationFailedException>(() => service.SavePreset(null, " ", content));
        var renamed = service.SavePreset(preset.Id, "Anime seasons", content with { Rules = [] });
        Assert.Empty(renamed.Content.Rules);
        Assert.Single(service.Presets());

        service.DeletePreset(preset.Id);
        Assert.Empty(service.Presets());
        Assert.Throws<LibraryException>(() => service.DeletePreset(preset.Id));
    }

    public void Dispose() => _bed.Dispose();
}
