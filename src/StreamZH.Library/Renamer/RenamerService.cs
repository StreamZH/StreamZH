// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Import;

namespace StreamZH.Library.Renamer;

/// <summary>
/// Tools → File renamer: lists the files of a folder, runs the rules over their names, shows old → new with conflicts and
/// renames as a logged job that can be undone. Files that belong to the library keep their library entry (paths, stored
/// file details) and trigger <see cref="MediaRenamed"/>. Nothing is ever overwritten.
/// </summary>
public sealed partial class RenamerService(
    MediaManagementSettings settings,
    RenamerCatalog catalog,
    RenamerRepository repository,
    RootFolderRepository rootFolders,
    MovieRepository movies,
    SeriesRepository series,
    EpisodeFileRepository episodeFiles,
    MediaFileDetailsRepository fileDetails,
    HistoryRepository history,
    ImportFileSystem files,
    RenamerProgress progress,
    JobQueue jobs,
    IEventBus events,
    AppPaths paths,
    ILogger<RenamerService> logger,
    TimeProvider? timeProvider = null)
{
    public const int MaxNameBytes = 255;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Whether "a.mkv" and "A.mkv" are the same file. Case-only renames then go through a temporary name (two steps).
    /// Windows and macOS: yes; Linux: no.
    /// </summary>
    public bool CaseInsensitiveFileSystem { get; init; } = !OperatingSystem.IsLinux();

    // ------------------------------------------------------------------ scan

    public ScanResult Scan(RenamerSource source, RenamerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        var resolved = Resolve(source);
        CheckFilter(filter);
        var (entries, total, truncated) = RenamerScanner.Scan(resolved.Folder, source, filter);
        return new ScanResult(resolved.Folder, total, entries.Count, entries, truncated, resolved.Hint.MovieId, resolved.Hint.SeriesId, resolved.Title);
    }

    // ------------------------------------------------------------------ preview

    public async Task<PreviewResult> PreviewAsync(RenamerRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(request, cancellationToken);
        return plan.Result;
    }

    private sealed class Row
    {
        public required ScanEntry Entry { get; init; }
        public required string OldPath { get; init; }
        public string NewName { get; set; } = string.Empty;
        public string NewFolder { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
        public RowStatus Status { get; set; }
        public string? Problem { get; set; }
        public string? Message { get; set; }
        public string? CompanionOf { get; init; }
        public MediaMatchInfo? Media { get; set; }
        public int? MovieId { get; set; }
        public int? SeriesId { get; set; }
    }

    private sealed record Plan(PreviewResult Result, IReadOnlyList<Row> Rows, string Folder);

    private async Task<Plan> PlanAsync(RenamerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Rules.Count > RuleEngine.MaxRules)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["rules"] = [$"Use at most {RuleEngine.MaxRules} rules."] });
        }

        var resolved = Resolve(request.Source);
        CheckFilter(request.Filter);
        var (entries, total, truncated) = RenamerScanner.Scan(resolved.Folder, request.Source, request.Filter);
        var ruleErrors = RuleEngine.Validate(request.Rules);
        var folderFormat = request.Options.FolderFormat.Trim();
        var folderFormatError = CheckFolderFormat(folderFormat);
        var namer = new MediaNamer(settings.Current, catalog, resolved.Hint);
        var excluded = request.Excluded.ToHashSet(StringComparer.Ordinal);
        var library = LibraryIndex.Build(resolved.Folder, catalog, rootFolders.All());

        var perFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<Row>();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var inFolder = perFolder.GetValueOrDefault(entry.Folder);
            perFolder[entry.Folder] = inFolder + 1;
            var oldPath = Full(resolved.Folder, entry.Key);
            var item = new RenameItem(entry.Name, entry.IsFolder, FolderName(resolved.Folder, entry), entry.Modified, entry.Size, index, inFolder);
            var row = new Row { Entry = entry, OldPath = oldPath, NewName = entry.Name, NewFolder = entry.Folder };
            MediaContext? media = null;
            try
            {
                for (var r = 0; r < request.Rules.Count; r++)
                {
                    var rule = request.Rules[r];
                    if (!rule.Enabled || ruleErrors.ContainsKey(r))
                    {
                        continue;
                    }

                    if (rule is MediaRule mediaRule)
                    {
                        var named = await namer.NameAsync(mediaRule, row.NewName, oldPath, item, cancellationToken);
                        if (named.Error is { } error)
                        {
                            throw new RuleException(error);
                        }

                        row.NewName = named.Name ?? row.NewName;
                        media = named.Context ?? media;
                        continue;
                    }

                    row.NewName = RuleEngine.Apply(rule, row.NewName, item);
                }

                if (folderFormat.Length > 0 && folderFormatError is null && !entry.IsFolder)
                {
                    var target = BuildFolder(folderFormat, item, media);
                    if (!PathComparer.Instance.Equals(target, item.Folder))
                    {
                        row.NewFolder = TargetFolder(entry.Folder, target, media);
                    }
                }
            }
            catch (RuleException ex)
            {
                row.Status = RowStatus.Error;
                row.Problem = RowProblems.Error;
                row.Message = ex.Message;
                row.NewName = entry.Name;
                row.NewFolder = entry.Folder;
            }

            row.Media = media?.Info;
            (row.MovieId, row.SeriesId) = library.OwnerOf(oldPath);
            rows.Add(row);
        }

        if (request.Options.Companions)
        {
            rows = AddCompanions(resolved.Folder, rows, library);
        }

        foreach (var row in rows)
        {
            row.NewPath = Full(resolved.Folder, row.NewFolder.Length == 0 ? row.NewName : $"{row.NewFolder}/{row.NewName}");
        }

        Resolve(rows, excluded, library);
        var result = new PreviewResult(
            resolved.Folder,
            total,
            entries.Count,
            truncated,
            [.. rows.Select(r => new PreviewRow(
                r.Entry.Key, r.Entry.Folder, r.Entry.Name, r.NewName, r.NewFolder, r.Entry.IsFolder, r.Entry.Size, r.Status, r.Problem, r.Message,
                r.CompanionOf, r.Media, r.MovieId, r.SeriesId))],
            new PreviewCounts(
                rows.Count,
                rows.Count(r => r.Status == RowStatus.Unchanged),
                rows.Count(r => r.Status == RowStatus.Rename),
                rows.Count(r => r.Status == RowStatus.Conflict),
                rows.Count(r => r.Status == RowStatus.Error),
                rows.Count(r => r.Status == RowStatus.Excluded)),
            ruleErrors,
            folderFormatError);
        return new Plan(result, rows, resolved.Folder);
    }

    /// <summary>"Show.S01E01.de.srt" follows "Show.S01E01.mkv" when the video is renamed (unless it is in the list itself).</summary>
    private static List<Row> AddCompanions(string root, List<Row> rows, LibraryIndex library)
    {
        var listed = rows.Select(r => r.OldPath).ToHashSet(PathComparer.Instance);
        var listings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var result = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(row);
            if (row.Entry.IsFolder || row.Status == RowStatus.Error || !DiskScanner.VideoExtensions.Contains(Path.GetExtension(row.Entry.Name))
                || (row.NewName == row.Entry.Name && row.NewFolder == row.Entry.Folder))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(row.OldPath)!;
            if (!listings.TryGetValue(directory, out var siblings))
            {
                try
                {
                    siblings = Directory.GetFiles(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    siblings = [];
                }

                listings[directory] = siblings;
            }

            var oldStem = RuleEngine.Split(row.Entry.Name, false).Stem;
            var newStem = RuleEngine.Split(row.NewName, false).Stem;
            foreach (var sibling in siblings.Order(NaturalComparer.Instance))
            {
                var name = Path.GetFileName(sibling);
                if (listed.Contains(sibling) || name.StartsWith('.') || !name.StartsWith(oldStem + ".", StringComparison.OrdinalIgnoreCase)
                    || DiskScanner.VideoExtensions.Contains(Path.GetExtension(name)))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(sibling);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                listed.Add(sibling);
                var key = RenamerPaths.Relative(root, sibling);
                var companion = new Row
                {
                    Entry = new ScanEntry(key, row.Entry.Folder, name, false, info.Length, info.LastWriteTimeUtc),
                    OldPath = sibling,
                    NewName = newStem + name[oldStem.Length..],
                    NewFolder = row.NewFolder,
                    CompanionOf = row.Entry.Key,
                };
                (companion.MovieId, companion.SeriesId) = library.OwnerOf(sibling);
                result.Add(companion);
            }
        }

        return result;
    }

    /// <summary>Statuses: unchanged, invalid names, library folders, excluded rows, duplicates and existing targets.</summary>
    private void Resolve(List<Row> rows, IReadOnlySet<string> excluded, LibraryIndex library)
    {
        foreach (var row in rows.Where(r => r.Status != RowStatus.Error))
        {
            if (string.Equals(row.NewPath, row.OldPath, StringComparison.Ordinal))
            {
                row.Status = RowStatus.Unchanged;
                continue;
            }

            if (NameProblem(row.NewName) is { } problem)
            {
                Conflict(row, problem.Code, problem.Message);
                continue;
            }

            if (OperatingSystem.IsWindows() && row.NewPath.Length > 259 || Encoding.UTF8.GetByteCount(row.NewPath) > 4000)
            {
                Conflict(row, RowProblems.TooLong, "The full path is too long.");
                continue;
            }

            if (row.Entry.IsFolder && library.IsLibraryFolder(row.OldPath))
            {
                Conflict(row, RowProblems.LibraryFolder, "This folder holds a library movie or series. Rename it in the library.");
                continue;
            }

            var parentExcluded = row.CompanionOf is { } parent && excluded.Contains(parent);
            row.Status = excluded.Contains(row.Entry.Key) || parentExcluded ? RowStatus.Excluded : RowStatus.Rename;
        }

        var comparer = CaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var group in rows.Where(r => r.Status == RowStatus.Rename).GroupBy(r => r.NewPath, comparer).Where(g => g.Count() > 1))
        {
            foreach (var row in group)
            {
                Conflict(row, RowProblems.Duplicate, $"{group.Count()} files would get this name.");
            }
        }

        // A target that exists is only fine when the file there moves away in this run (A → B, B → C).
        bool changed;
        do
        {
            changed = false;
            var moving = rows.Where(r => r.Status == RowStatus.Rename).Select(r => r.OldPath).ToHashSet(comparer);
            var renamed = rows.Where(r => r.Status == RowStatus.Rename).Select(r => r.Entry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows.Where(r => r.Status == RowStatus.Rename && r.CompanionOf is { } parent && !renamed.Contains(parent)))
            {
                Conflict(row, RowProblems.Follows, "Stays with its video, which is not renamed.");
                changed = true;
            }

            foreach (var row in rows.Where(r => r.Status == RowStatus.Rename))
            {
                if (comparer.Equals(row.NewPath, row.OldPath) || !(File.Exists(row.NewPath) || Directory.Exists(row.NewPath)) || moving.Contains(row.NewPath))
                {
                    continue;
                }

                Conflict(row, RowProblems.Exists, "A file or folder with this name already exists.");
                changed = true;
            }
        }
        while (changed);

        static void Conflict(Row row, string code, string message)
        {
            row.Status = RowStatus.Conflict;
            row.Problem = code;
            row.Message = message;
        }
    }

    /// <summary>Names that cannot be used: empty, characters Windows does not allow, reserved names, more than 255 bytes.</summary>
    public static (string Code, string Message)? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Trim().Length == 0 || name is "." or "..")
        {
            return (RowProblems.Empty, "The new name is empty.");
        }

        if (name.IndexOfAny(['/', '\\', '<', '>', ':', '"', '|', '?', '*']) >= 0 || name.Any(char.IsControl))
        {
            return (RowProblems.Invalid, "Contains characters that are not allowed in names: / \\ < > : \" | ? *");
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return (RowProblems.Invalid, "Names cannot end with a space or a dot (Windows).");
        }

        if (name.StartsWith('.'))
        {
            return (RowProblems.Invalid, "Names starting with a dot are hidden.");
        }

        if (ReservedNameRegex().IsMatch(name))
        {
            return (RowProblems.Invalid, "This name is reserved on Windows.");
        }

        return Encoding.UTF8.GetByteCount(name) > MaxNameBytes ? (RowProblems.TooLong, $"The name is longer than {MaxNameBytes} bytes.") : null;
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Plans the run from a fresh preview and queues it as a job; returns the run id.</summary>
    public async Task<int> QueueApplyAsync(RenamerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await PlanAsync(request, cancellationToken);
        var errors = plan.Result.RuleErrors.ToDictionary(e => $"rules[{e.Key.ToString(CultureInfo.InvariantCulture)}]", e => new[] { e.Value });
        if (plan.Result.FolderFormatError is { } folderError)
        {
            errors["options.folderFormat"] = [folderError];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var renames = plan.Rows.Where(r => r.Status == RowStatus.Rename).ToList();
        if (renames.Count == 0)
        {
            throw new LibraryException("Nothing to rename.", LibraryError.Invalid);
        }

        // Files first (in list order), then folders from the deepest up, so paths stay valid while renaming.
        var ordered = renames.Where(r => !r.Entry.IsFolder)
            .Concat(renames.Where(r => r.Entry.IsFolder).OrderByDescending(r => Depth(r.OldPath)).ThenBy(r => r.Entry.Key, NaturalComparer.Instance))
            .Select((r, i) => new RenamerRunItem(i, RunItemKind.Rename, r.OldPath, r.NewPath, r.Entry.IsFolder, RunItemState.Planned, null))
            .ToList();
        var summary = Summary(request.Rules, request.Options);
        var runId = repository.AddRun(plan.Folder, summary, request.Rules, ordered, _time.GetUtcNow().UtcDateTime);
        var title = $"Rename {Count(ordered.Count, "file")}";
        jobs.Enqueue(new Job($"renamer-{runId}", title, (services, ct) => services.GetRequiredService<RenamerService>().ExecuteAsync(runId, ct)));
        logger.LogInformation("Queued rename run {Run}: {Count} item(s) in {Folder}", runId, ordered.Count, plan.Folder);
        return runId;
    }

    /// <summary>Queues the undo of a finished run.</summary>
    public int QueueUndo(int runId)
    {
        var run = repository.FindRun(runId) ?? throw new LibraryException("Run not found.", LibraryError.NotFound);
        if (run.State is not (RunState.Completed or RunState.Partial or RunState.Failed) || run.Done == 0)
        {
            throw new LibraryException(run.State switch
            {
                RunState.Undone => "This run was already undone.",
                RunState.Undoing => "This run is being undone.",
                RunState.Queued or RunState.Running => "Wait until the run has finished.",
                _ => "Nothing to undo in this run.",
            }, LibraryError.Conflict);
        }

        repository.UpdateRun(runId, RunState.Undoing, run.Done, run.Failed, null, null);
        jobs.Enqueue(new Job($"renamer-undo-{runId}", $"Undo rename of {Count(run.Done, "file")}", (services, ct) => services.GetRequiredService<RenamerService>().UndoAsync(runId, ct)));
        return runId;
    }

    public IReadOnlyList<RenamerRun> History(int limit = 50) => repository.Runs(Math.Clamp(limit, 1, RenamerRepository.MaxRunsKept));

    public RenamerRunDetail Run(int runId)
    {
        var run = repository.FindRun(runId) ?? throw new LibraryException("Run not found.", LibraryError.NotFound);
        return new RenamerRunDetail(run, repository.Items(runId));
    }

    private sealed record Op(int Order, string From, string To, bool IsFolder);

    /// <summary>The job: renames the planned items, updates the library and logs every step.</summary>
    public async Task ExecuteAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = repository.FindRun(runId) ?? throw new LibraryException("Run not found.", LibraryError.NotFound);
        var items = repository.Items(runId).Where(i => i.Kind == RunItemKind.Rename && i.State == RunItemState.Planned).ToList();
        repository.UpdateRun(runId, RunState.Running, 0, 0, null, null);
        await progress.StartAsync(runId, $"Renaming {Count(items.Count, "file")}", items.Count, undo: false);
        catalog.Refresh();
        var library = LibraryIndex.Build(run.Folder, catalog, rootFolders.All());
        var done = new List<Op>();
        var failed = 0;
        var nextOrder = repository.Items(runId).Select(i => i.Order).DefaultIfEmpty(-1).Max() + 1;
        string? fatal = null;
        try
        {
            var ops = items.Select(i => new Op(i.Order, i.OldPath, i.NewPath, i.IsFolder)).ToList();
            foreach (var stage in ops.GroupBy(o => o.IsFolder ? Depth(o.From) : int.MaxValue).OrderByDescending(g => g.Key))
            {
                var (ok, bad, created) = await RunStageAsync(runId, [.. stage], nextOrder, cancellationToken);
                done.AddRange(ok);
                failed += bad;
                nextOrder += created;
            }

            RemoveEmptiedFolders(runId, run.Folder, done, library, nextOrder);
        }
        catch (OperationCanceledException)
        {
            fatal = "StreamZH stopped during the run.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibraryException)
        {
            fatal = ex.Message;
            logger.LogError(ex, "Rename run {Run} stopped: {Message}", runId, ex.Message);
        }
        finally
        {
            var affected = UpdateLibrary(library, done, run.Folder);
            var state = fatal is not null ? RunState.Failed : failed == 0 ? RunState.Completed : done.Count == 0 ? RunState.Failed : RunState.Partial;
            repository.UpdateRun(runId, state, done.Count, failed, fatal, _time.GetUtcNow().UtcDateTime);
            await progress.FinishAsync();
            await PublishAsync(affected, CancellationToken.None);
            logger.LogInformation("Rename run {Run}: {Done} renamed, {Failed} failed", runId, done.Count, failed);
        }
    }

    /// <summary>The undo job: every renamed item back to its old name (in reverse order), created folders removed when empty.</summary>
    public async Task UndoAsync(int runId, CancellationToken cancellationToken = default)
    {
        var run = repository.FindRun(runId) ?? throw new LibraryException("Run not found.", LibraryError.NotFound);
        var all = repository.Items(runId);
        var items = all.Where(i => i.Kind == RunItemKind.Rename && i.State == RunItemState.Done).ToList();
        await progress.StartAsync(runId, $"Undoing rename of {Count(items.Count, "file")}", items.Count, undo: true);
        catalog.Refresh();
        var library = LibraryIndex.Build(run.Folder, catalog, rootFolders.All());
        var done = new List<Op>();
        var failed = 0;
        string? fatal = null;
        try
        {
            var ops = items.Select(i => new Op(i.Order, i.NewPath, i.OldPath, i.IsFolder)).ToList();
            RecreateRemovedFolders(runId, all);

            // Stages in reverse: folders from the top down, then the files.
            foreach (var stage in ops.GroupBy(o => o.IsFolder ? Depth(o.To) : int.MaxValue).OrderBy(g => g.Key))
            {
                var (ok, bad, _) = await RunStageAsync(runId, [.. stage.OrderByDescending(o => o.Order)], int.MinValue, cancellationToken, undo: true);
                done.AddRange(ok);
                failed += bad;
            }

            foreach (var created in all.Where(i => i.Kind == RunItemKind.CreateFolder && i.State == RunItemState.Done).OrderByDescending(i => i.Order))
            {
                try
                {
                    if (Directory.Exists(created.NewPath) && !Directory.EnumerateFileSystemEntries(created.NewPath).Any())
                    {
                        Directory.Delete(created.NewPath);
                    }

                    repository.SetItemState(runId, created.Order, RunItemState.Undone, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not remove {Folder}: {Message}", created.NewPath, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            fatal = "StreamZH stopped during the undo.";
        }
        finally
        {
            var affected = UpdateLibrary(library, done, run.Folder);
            var now = _time.GetUtcNow().UtcDateTime;
            var remaining = items.Count - done.Count;
            repository.UpdateRun(
                runId,
                fatal is null && remaining == 0 ? RunState.Undone : RunState.Partial,
                remaining,
                run.Failed + failed,
                fatal ?? (failed > 0 ? $"{Count(failed, "file")} could not be put back." : null),
                now,
                done.Count > 0 ? now : null);
            await progress.FinishAsync();
            await PublishAsync(affected, CancellationToken.None);
            logger.LogInformation("Undo of rename run {Run}: {Done} put back, {Failed} failed", runId, done.Count, failed);
        }
    }

    /// <summary>
    /// Renames one stage (all files, or the folders of one depth). Items whose target is another item's source (A → B,
    /// B → A) and case-only renames on Windows and macOS go through a temporary name; never overwrites anything.
    /// </summary>
    private async Task<(List<Op> Done, int Failed, int Created)> RunStageAsync(int runId, List<Op> ops, int nextOrder, CancellationToken ct, bool undo = false)
    {
        var comparer = CaseInsensitiveFileSystem ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = ops.Select(o => o.From).ToHashSet(comparer);
        var done = new List<Op>();
        var failed = 0;
        var created = 0;
        var temps = new List<(Op Op, string Temp)>();

        bool NeedsTemp(Op op) =>
            (CaseInsensitiveFileSystem && string.Equals(op.From, op.To, StringComparison.OrdinalIgnoreCase))
            || (sources.Contains(op.To) && !comparer.Equals(op.From, op.To));

        async Task FailAsync(Op op, string message)
        {
            failed++;
            repository.SetItemState(runId, op.Order, RunItemState.Failed, message);
            if (!undo)
            {
                repository.CountItem(runId, success: false);
            }

            logger.LogWarning("Rename of {From} failed: {Message}", op.From, message);
            await progress.AdvanceAsync();
        }

        async Task DoneAsync(Op op)
        {
            done.Add(op);
            repository.SetItemState(runId, op.Order, undo ? RunItemState.Undone : RunItemState.Done, null);
            if (!undo)
            {
                repository.CountItem(runId, success: true);
            }

            await progress.AdvanceAsync();
        }

        string? Folder(Op op)
        {
            var (error, made) = EnsureFolder(op, runId, nextOrder + created, undo);
            if (made)
            {
                created++;
            }

            return error;
        }

        // 1: items that need two steps move to a temporary name first.
        foreach (var op in ops.Where(NeedsTemp))
        {
            ct.ThrowIfCancellationRequested();
            var temp = Path.Combine(
                Path.GetDirectoryName(op.From)!,
                $".streamzh-rename-{runId.ToString(CultureInfo.InvariantCulture)}-{(op.Order & 0x7fffffff).ToString(CultureInfo.InvariantCulture)}.tmp");
            if (!Exists(op.From))
            {
                await FailAsync(op, "The file is no longer there.");
            }
            else if (Exists(temp))
            {
                await FailAsync(op, $"The temporary name {Path.GetFileName(temp)} is taken.");
            }
            else if (Move(op.From, temp, op.IsFolder) is { } error)
            {
                await FailAsync(op, error);
            }
            else
            {
                temps.Add((op, temp));
            }
        }

        // 2: everything else directly.
        foreach (var op in ops.Where(o => !NeedsTemp(o)))
        {
            ct.ThrowIfCancellationRequested();
            if (!Exists(op.From))
            {
                await FailAsync(op, "The file is no longer there.");
            }
            else if (Exists(op.To))
            {
                await FailAsync(op, "A file or folder with the new name already exists.");
            }
            else if (Folder(op) is { } folderError)
            {
                await FailAsync(op, folderError);
            }
            else if (Move(op.From, op.To, op.IsFolder) is { } error)
            {
                await FailAsync(op, error);
            }
            else
            {
                await DoneAsync(op);
            }
        }

        // 3: the temporary names to their targets (or back, when the target is taken).
        foreach (var (op, temp) in temps)
        {
            var problem = Exists(op.To) ? "A file or folder with the new name already exists." : Folder(op) ?? Move(temp, op.To, op.IsFolder);
            if (problem is null)
            {
                await DoneAsync(op);
                continue;
            }

            Move(temp, op.From, op.IsFolder);
            await FailAsync(op, problem);
        }

        // Keep the order in which they happened for the library update.
        done.Sort((a, b) => undo ? b.Order.CompareTo(a.Order) : a.Order.CompareTo(b.Order));
        return (done, failed, created);
    }

    /// <summary>Creates the target folder ("Season 01") when it is missing and logs it for the undo.</summary>
    private (string? Error, bool Created) EnsureFolder(Op op, int runId, int order, bool undo)
    {
        var folder = Path.GetDirectoryName(op.To)!;
        if (Directory.Exists(folder))
        {
            return (null, false);
        }

        if (undo)
        {
            return ("The old folder no longer exists.", false);
        }

        try
        {
            files.CreateFolder(folder, settings.Current);
        }
        catch (ImportException ex)
        {
            return (ex.Message, false);
        }

        repository.AddItems(runId, [new RenamerRunItem(order, RunItemKind.CreateFolder, string.Empty, folder, true, RunItemState.Done, null)]);
        return (null, true);
    }

    /// <summary>
    /// Folders inside the source that this run emptied ("Season 1" after its files moved to "Season 01") are removed,
    /// deepest first, and logged for the undo. A folder left with only system junk (Thumbs.db, desktop.ini, .DS_Store) counts
    /// as empty: the junk files go with it (the undo recreates the folder, not the junk). Folders that still hold anything
    /// else, library item folders and the source folder itself stay.
    /// </summary>
    private void RemoveEmptiedFolders(int runId, string root, IReadOnlyList<Op> done, LibraryIndex library, int order)
    {
        var targets = done.Where(o => o.IsFolder).Select(o => o.To).ToHashSet(PathComparer.Instance);
        var candidates = new HashSet<string>(PathComparer.Instance);
        foreach (var op in done.Where(o => !o.IsFolder))
        {
            for (var folder = Path.GetDirectoryName(op.From); folder is not null && IsBelow(folder, root); folder = Path.GetDirectoryName(folder))
            {
                // Folders renamed later in the run: the emptied folder now sits under the new name.
                candidates.Add(Translate(folder, done));
            }
        }

        var removed = new List<RenamerRunItem>();
        foreach (var folder in candidates.Where(f => IsBelow(f, root) && !targets.Contains(f)).OrderByDescending(Depth).ThenBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (!Directory.Exists(folder) || library.IsLibraryFolder(folder) || !ImportFileSystem.OnlyJunk(folder, out var junk))
                {
                    continue;
                }

                foreach (var file in junk)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                Directory.Delete(folder);
                removed.Add(new RenamerRunItem(order + removed.Count, RunItemKind.RemoveFolder, folder, string.Empty, true, RunItemState.Done, null));
                logger.LogDebug("Removed the emptied folder {Folder}", folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove the emptied folder {Folder}: {Message}", folder, ex.Message);
            }
        }

        if (removed.Count > 0)
        {
            repository.AddItems(runId, removed);
        }

        static bool IsBelow(string folder, string root) => RenamerPaths.IsInside(folder, root) && !PathComparer.Instance.Equals(folder, root);
    }

    /// <summary>The undo puts the folders the run removed back first (from the top down), so the files can return.</summary>
    private void RecreateRemovedFolders(int runId, IReadOnlyList<RenamerRunItem> all)
    {
        foreach (var removed in all.Where(i => i.Kind == RunItemKind.RemoveFolder && i.State == RunItemState.Done).OrderBy(i => Depth(i.OldPath)).ThenBy(i => i.Order))
        {
            try
            {
                files.CreateFolder(removed.OldPath, settings.Current);
                repository.SetItemState(runId, removed.Order, RunItemState.Undone, null);
            }
            catch (ImportException ex)
            {
                repository.SetItemState(runId, removed.Order, RunItemState.Failed, ex.Message);
                logger.LogWarning("Could not recreate {Folder}: {Message}", removed.OldPath, ex.Message);
            }
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? Move(string from, string to, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImportFileSystem.Describe(ex, Path.GetDirectoryName(to) ?? to);
        }
    }

    // ------------------------------------------------------------------ library

    private sealed record Affected(HashSet<int> Movies, HashSet<int> Series);

    /// <summary>Library files that were renamed (or are in renamed folders) get their new path; stored details move with them.</summary>
    private Affected UpdateLibrary(LibraryIndex library, IReadOnlyList<Op> done, string folder)
    {
        var affected = new Affected([], []);
        if (done.Count == 0)
        {
            return affected;
        }

        foreach (var op in done)
        {
            if (op.IsFolder)
            {
                repository.MoveDetailsUnder(op.From, op.To);
            }
            else
            {
                fileDetails.Move(op.From, op.To);
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var (path, owner) in library.Files)
        {
            var moved = Translate(path, done);
            if (string.Equals(moved, path, StringComparison.Ordinal))
            {
                continue;
            }

            var itemFolder = owner.MovieId is { } movieId ? movies.Find(movieId)?.FolderPath : series.Find(owner.SeriesId!.Value)?.FolderPath;
            if (itemFolder is null || !RenamerPaths.IsInside(moved, itemFolder))
            {
                logger.LogWarning("{Path} was moved out of its library folder; the library entry was not changed", moved);
                continue;
            }

            var relative = Path.GetRelativePath(itemFolder, moved);
            var info = new FileInfo(moved);
            var file = new MediaFile(relative, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow);
            if (owner.MovieId is { } id && movies.Find(id) is { } movie)
            {
                movie.File = file;
                movies.Update(movie);
                affected.Movies.Add(id);
            }
            else if (owner.SeriesId is { } seriesId)
            {
                episodeFiles.SetFile(owner.EpisodeIds, file);
                affected.Series.Add(seriesId);
            }

            history.Add(new HistoryItem
            {
                Date = now,
                EventType = HistoryEventType.Renamed,
                MediaType = owner.MovieId is not null ? MediaType.Movie : MediaType.Series,
                MovieId = owner.MovieId,
                SeriesId = owner.SeriesId,
                EpisodeIds = owner.EpisodeIds,
                SourceTitle = Path.GetFileName(path),
                Data = new Dictionary<string, string?>
                {
                    ["from"] = Path.GetRelativePath(itemFolder, path),
                    ["to"] = relative,
                    ["tool"] = "renamer",
                },
            });
        }

        logger.LogDebug("Library entries updated after renaming in {Folder}: {Movies} movie(s), {Series} series", folder, affected.Movies.Count, affected.Series.Count);
        return affected;
    }

    private static string Translate(string path, IReadOnlyList<Op> done)
    {
        foreach (var op in done)
        {
            if (!op.IsFolder && PathComparer.Instance.Equals(path, op.From))
            {
                path = op.To;
            }
            else if (op.IsFolder && RenamerPaths.IsInside(path, op.From))
            {
                path = op.To + path[op.From.Length..];
            }
        }

        return path;
    }

    private async Task PublishAsync(Affected affected, CancellationToken cancellationToken)
    {
        foreach (var id in affected.Movies)
        {
            if (movies.Find(id) is { } movie)
            {
                await events.PublishAsync(new MediaRenamed(MediaType.Movie, id, null, movie.FolderPath), cancellationToken);
                await events.PublishAsync(new LibraryChanged(MediaType.Movie, id), cancellationToken);
            }
        }

        foreach (var id in affected.Series)
        {
            if (series.Find(id) is { } show)
            {
                await events.PublishAsync(new MediaRenamed(MediaType.Series, null, id, show.FolderPath), cancellationToken);
                await events.PublishAsync(new LibraryChanged(MediaType.Series, id), cancellationToken);
            }
        }
    }

    // ------------------------------------------------------------------ presets

    public IReadOnlyList<RenamerPreset> Presets() => repository.Presets();

    public RenamerPreset SavePreset(int? id, string? name, RenamerPresetContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var trimmed = name?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>();
        if (trimmed.Length == 0)
        {
            errors["name"] = ["Enter a name."];
        }
        else if (trimmed.Length > 100)
        {
            errors["name"] = ["Use at most 100 characters."];
        }
        else if (repository.FindPreset(trimmed) is { } existing && existing.Id != id)
        {
            errors["name"] = ["A preset with this name exists."];
        }

        if (content.Rules.Count > RuleEngine.MaxRules)
        {
            errors["rules"] = [$"Use at most {RuleEngine.MaxRules} rules."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (id is { } presetId)
        {
            if (!repository.UpdatePreset(presetId, trimmed, content, now))
            {
                throw new LibraryException("Preset not found.", LibraryError.NotFound);
            }

            return repository.FindPreset(presetId)!;
        }

        return repository.FindPreset(repository.AddPreset(trimmed, content, now))!;
    }

    public void DeletePreset(int id)
    {
        if (!repository.DeletePreset(id))
        {
            throw new LibraryException("Preset not found.", LibraryError.NotFound);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Source(string Folder, MediaHint Hint, string? Title);

    /// <summary>The folder to work in, and the library item it belongs to (by choice or because it is that item's folder).</summary>
    private Source Resolve(RenamerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? path = source.Path;
        if (source.MovieId is { } movieId)
        {
            path = (movies.Find(movieId) ?? throw new LibraryException("Movie not found.", LibraryError.NotFound)).FolderPath;
        }
        else if (source.SeriesId is { } seriesId)
        {
            path = (series.Find(seriesId) ?? throw new LibraryException("Series not found.", LibraryError.NotFound)).FolderPath;
        }

        if (source.Depth is < 1 or > RenamerSource.MaxDepth)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["source.depth"] = [$"Use 1 to {RenamerSource.MaxDepth} levels."] });
        }

        var (folder, error) = RenamerPaths.Check(path, paths.DataDirectory);
        if (folder is null)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["source.path"] = [error!] });
        }

        var movie = source.MovieId is { } m ? catalog.FindMovie(m) : catalog.Movies.FirstOrDefault(x => x.FolderPath.Length > 0 && RenamerPaths.IsInside(folder, x.FolderPath));
        if (movie is not null && source.SeriesId is null)
        {
            return new Source(folder, new MediaHint(movie.Id, null), $"{movie.Title} ({movie.Year.ToString(CultureInfo.InvariantCulture)})");
        }

        var show = source.SeriesId is { } s ? catalog.FindSeries(s) : catalog.Series.FirstOrDefault(x => x.FolderPath.Length > 0 && RenamerPaths.IsInside(folder, x.FolderPath));
        return show is not null
            ? new Source(folder, new MediaHint(null, show.Id), show.Title)
            : new Source(folder, new MediaHint(null, null), null);
    }

    private static void CheckFilter(RenamerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (RenamerScanner.Check(filter) is { } error)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { [$"filter.{error.Field}"] = [error.Message] });
        }
    }

    /// <summary>Null when the "move into folder" format can be used.</summary>
    public static string? CheckFolderFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        if (format.IndexOfAny(['/', '\\']) >= 0)
        {
            return "Only one folder level; remove / and \\.";
        }

        if (format.Length > 200)
        {
            return "Use at most 200 characters.";
        }

        var text = GenericTokenRegex().Replace(format, "x");
        if (!MediaTokenRegex().IsMatch(text))
        {
            return text.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '{', '}']) >= 0 ? "Remove the characters < > : \" | ? * and stray braces." : null;
        }

        var errors = new[] { NamingFormatKind.SeasonFolder, NamingFormatKind.MovieFolder, NamingFormatKind.SeriesFolder }
            .Select(kind => FileNameBuilder.ValidateFormat(text, kind))
            .ToList();
        if (errors.Any(e => e is null))
        {
            return null;
        }

        return errors.FirstOrDefault(e => e!.StartsWith("Unknown token", StringComparison.Ordinal) || e.StartsWith("A brace", StringComparison.Ordinal))
            ?? "Use season, series or movie tokens, e.g. Season {season:00}.";
    }

    /// <summary>The subfolder from the folder format: generic tokens, then the naming tokens of the matched movie or series.</summary>
    private string BuildFolder(string format, RenameItem item, MediaContext? media)
    {
        var text = RuleEngine.ExpandTokens(format, item);
        string name;
        if (!MediaTokenRegex().IsMatch(text))
        {
            name = new string([.. text.Where(c => !char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '|' or '?' or '*'))]).Trim(' ', '.');
        }
        else if (media is null)
        {
            throw new RuleException("The folder format uses media tokens; add a media rule.");
        }
        else
        {
            var values = settings.Current with { UseSeasonFolders = true, SeasonFolderFormat = text, MovieFolderFormat = text, SeriesFolderFormat = text };
            var builder = new FileNameBuilder(values);
            name = media.Movie is { } movie
                ? builder.MovieFolderName(movie)
                : builder.SeasonFolderName(media.Series!, media.Season ?? 1) ?? string.Empty;
        }

        return name.Length == 0 ? throw new RuleException("The folder format gave an empty folder name.") : name;
    }

    /// <summary>
    /// Where "move into a folder" puts a file: a subfolder of its folder, or, when the file already sits in a season
    /// folder and the target is one too ("Season 1" → "Season 01"), the sibling next to it. Never above the source.
    /// </summary>
    private string TargetFolder(string current, string target, MediaContext? media)
    {
        if (current.Length == 0)
        {
            return target;
        }

        var slash = current.LastIndexOf('/');
        var name = current[(slash + 1)..];
        if (!IsSeasonFolder(name, media) || !IsSeasonFolder(target, media))
        {
            return $"{current}/{target}";
        }

        return slash < 0 ? target : $"{current[..slash]}/{target}";
    }

    /// <summary>"Season 1", "Staffel 02", "S3", "Specials", or what the season and specials folder formats give for this series.</summary>
    private bool IsSeasonFolder(string name, MediaContext? media)
    {
        if (SeasonFolderRegex().IsMatch(name))
        {
            return true;
        }

        var values = settings.Current with { UseSeasonFolders = true };
        if (media?.Series is not { } show)
        {
            var specials = values.SpecialsFolderFormat.Trim();
            return specials.Length > 0 && !specials.Contains('{', StringComparison.Ordinal) && PathComparer.Instance.Equals(name, specials);
        }

        var builder = new FileNameBuilder(values);
        if (PathComparer.Instance.Equals(name, builder.SeasonFolderName(show, 0) ?? string.Empty))
        {
            return true;
        }

        return NumberRegex().Matches(name).Any(m => int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var season)
            && PathComparer.Instance.Equals(name, builder.SeasonFolderName(show, season) ?? string.Empty));
    }

    private static string Summary(IReadOnlyList<RenameRule> rules, RenamerOptions options)
    {
        var parts = rules.Where(r => r.Enabled).Select(r => r switch
        {
            ReplaceRule x => $"Replace \"{Short(x.Find)}\"",
            RemoveRule x => x.Mode switch
            {
                RemoveMode.Brackets => "Remove brackets",
                RemoveMode.Separators => "Dots to spaces",
                RemoveMode.Spaces => "Remove double spaces",
                RemoveMode.Between => $"Remove {Short(x.StartMarker)}…{Short(x.EndMarker)}",
                _ => "Remove characters",
            },
            InsertRule x => $"Insert \"{Short(x.Text)}\"",
            CaseRule x => x.Mode switch { CaseMode.Lower => "lower case", CaseMode.Upper => "UPPER CASE", CaseMode.Sentence => "Sentence case", _ => "Title Case" },
            CounterRule => "Counter",
            ExtensionRule x => x.Mode == ExtensionMode.Change ? $"Extension .{x.NewExtension.Trim().TrimStart('.')}" : "Extension",
            TrimRule => "Trim",
            MediaRule x => x.Kind switch { MediaKind.Movie => "Media (movies)", MediaKind.Episode => "Media (episodes)", _ => "Media" },
            _ => "Rule",
        }).ToList();
        if (options.FolderFormat.Trim().Length > 0)
        {
            parts.Add($"into \"{Short(options.FolderFormat.Trim())}\"");
        }

        return parts.Count == 0 ? "No rules" : string.Join(" · ", parts);

        static string Short(string text) => text.Length > 30 ? text[..29] + "…" : text;
    }

    private static string Full(string root, string key) => Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));

    private static string FolderName(string root, ScanEntry entry) =>
        Path.GetFileName(entry.Folder.Length == 0 ? root : Path.Combine(root, entry.Folder.Replace('/', Path.DirectorySeparatorChar)));

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";

    [GeneratedRegex(@"\{(?:name|ext|folder|date|time|year|index)\}", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTokenRegex();

    [GeneratedRegex(@"(?<!\{)\{(?!\{)[^{}]+\}")]
    private static partial Regex MediaTokenRegex();

    [GeneratedRegex(@"^(?:(?:season|staffel|saison|temporada|stagione|series)[ ._-]*\d{1,4}|s\d{1,4}|specials?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"\d{1,4}")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedNameRegex();
}

/// <summary>Which library movie or episodes a file belongs to, and which folders are library item folders.</summary>
internal sealed class LibraryIndex
{
    public sealed record Owner(int? MovieId, int? SeriesId, IReadOnlyList<int> EpisodeIds);

    private readonly Dictionary<string, Owner> _files = new(PathComparer.Instance);
    private readonly List<string> _itemFolders = [];

    public IReadOnlyDictionary<string, Owner> Files => _files;

    /// <summary>The library items whose folder is inside <paramref name="folder"/> or contains it.</summary>
    public static LibraryIndex Build(string folder, RenamerCatalog catalog, IReadOnlyList<RootFolder> roots)
    {
        var index = new LibraryIndex();
        index._itemFolders.AddRange(roots.Select(r => Path.TrimEndingDirectorySeparator(r.Path)));
        bool Related(string itemFolder) => itemFolder.Length > 0 && (RenamerPaths.IsInside(itemFolder, folder) || RenamerPaths.IsInside(folder, itemFolder));
        foreach (var movie in catalog.Movies.Where(m => Related(m.FolderPath)))
        {
            index._itemFolders.Add(movie.FolderPath);
            if (movie.File is { } file)
            {
                index._files[Path.GetFullPath(Path.Combine(movie.FolderPath, file.RelativePath))] = new Owner(movie.Id, null, []);
            }
        }

        foreach (var show in catalog.Series.Where(s => Related(s.FolderPath)))
        {
            index._itemFolders.Add(show.FolderPath);
            foreach (var group in catalog.Episodes(show.Id).Where(e => e.File is not null).GroupBy(e => e.File!.RelativePath))
            {
                index._files[Path.GetFullPath(Path.Combine(show.FolderPath, group.Key))] = new Owner(null, show.Id, [.. group.Select(e => e.Id)]);
            }
        }

        return index;
    }

    public (int? MovieId, int? SeriesId) OwnerOf(string path) =>
        _files.TryGetValue(path, out var owner) ? (owner.MovieId, owner.SeriesId) : (null, null);

    /// <summary>A root folder, a movie or series folder, or a folder that contains one.</summary>
    public bool IsLibraryFolder(string path) => _itemFolders.Any(f => RenamerPaths.IsInside(f, path));
}
