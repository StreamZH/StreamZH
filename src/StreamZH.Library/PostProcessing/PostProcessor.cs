// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.PostProcessing;

/// <summary>
/// Verify (native par2 reader, with deobfuscation) → repair (external par2, only when needed) → extract (external 7-Zip)
/// → move the wanted files to the target and delete the work folder. On failure the work folder is kept as it is.
/// </summary>
public sealed partial class PostProcessor(PostProcessTools tools, IToolProcessRunner runner, ILogger<PostProcessor> logger) : IPostProcessor
{
    /// <summary>Files with "sample" in the name below this size are dropped (same idea as Sonarr).</summary>
    public const long SampleMaxBytes = 50L * 1024 * 1024;

    public const string Par2MissingError = "Repair needed but par2 is not installed (Settings → Downloads → Tools)";
    public const string SevenZipMissingError = "Archives found but 7-Zip is not installed (Settings → Downloads → Tools)";

    private const string StagingPrefix = ".streamzh-unpack-";

    /// <summary>Never moved to the target: release info, checksums, index files and anything executable.</summary>
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".sfv", ".srr", ".srs", ".nzb", ".par2", ".md5", ".url", ".lnk", ".db",
        ".exe", ".scr", ".bat", ".cmd", ".com", ".pif", ".vbs", ".js", ".jar", ".msi", ".ps1",
    };

    public async Task<PostProcessResult> RunAsync(PostProcessJob job, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        var work = Path.GetFullPath(job.WorkFolder);
        var target = Path.GetFullPath(job.TargetFolder);
        var staging = Path.Combine(target, StagingPrefix + job.DownloadId);
        var targetExisted = Directory.Exists(target);

        if (!Directory.Exists(work))
        {
            return Failed(job, $"Download folder not found: {work}");
        }

        if (IsSameOrInside(work, target))
        {
            return Failed(job, "The target folder must not be the download folder or inside it.");
        }

        logger.LogInformation("Post-processing {Title} in {Folder}", job.Title, work);

        try
        {
            var verify = await VerifyAndRepairAsync(job, work, progress, cancellationToken).ConfigureAwait(false);
            if (verify.Error is not null)
            {
                return Failed(job, verify.Error);
            }

            var extract = await ExtractAsync(job, work, staging, progress, cancellationToken).ConfigureAwait(false);
            if (extract.Error is not null)
            {
                RemoveStaging(staging, target, targetExisted);
                return Failed(job, extract.Error);
            }

            progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 0, "Moving files"));
            var files = MoveToTarget(work, staging, target, extract.Volumes, verify.Par2Files);
            RemoveStaging(staging, target, targetExisted: true);

            if (files.Count == 0)
            {
                RemoveStaging(staging, target, targetExisted);
                return Failed(job, "No files left to import after post-processing (only samples, .nfo or .par2 files).");
            }

            progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 0.8, "Deleting download folder"));
            DeleteWorkFolder(work);
            progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 1, null));

            logger.LogInformation("Post-processing of {Title} done: {Count} file(s) in {Target}{Repaired}", job.Title, files.Count, target, verify.Repaired ? " (repaired)" : string.Empty);
            return new PostProcessResult(true, null, target, files, verify.Repaired);
        }
        catch (OperationCanceledException)
        {
            RemoveStaging(staging, target, targetExisted);
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            RemoveStaging(staging, target, targetExisted);
            logger.LogWarning(e, "Post-processing of {Title} failed", job.Title);
            return Failed(job, e.Message);
        }
    }

    /// <summary>The password from the job, else the last {{password}} in the title (common Usenet convention).</summary>
    public static string? PasswordFor(PostProcessJob job)
    {
        if (!string.IsNullOrEmpty(job.Password))
        {
            return job.Password;
        }

        var matches = TitlePasswordRegex().Matches(job.Title);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    private sealed record VerifyOutcome(string? Error, bool Repaired, IReadOnlySet<string> Par2Files);

    private async Task<VerifyOutcome> VerifyAndRepairAsync(PostProcessJob job, string work, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new PostProcessProgress(PostProcessStage.Verifying, 0, "Verifying files"));

        var topLevel = Directory.GetFiles(work).Where(f => !IsStaging(f)).ToList();
        var par2Files = topLevel.Where(Par2Reader.IsPar2File).ToList();
        var par2Set = new HashSet<string>(par2Files, StringComparer.Ordinal);
        if (par2Files.Count == 0)
        {
            logger.LogInformation("{Title}: no par2 files, skipping verification", job.Title);
            progress.Report(new PostProcessProgress(PostProcessStage.Verifying, 1, "No par2 files"));
            return new VerifyOutcome(null, false, par2Set);
        }

        var catalog = Par2Reader.ReadFiles(par2Files, verifyRecoveryData: false, cancellationToken);
        var sets = catalog.Sets.Values.Where(s => s.HasMain && s.RecoverableFiles.Any()).ToList();
        if (sets.Count == 0)
        {
            logger.LogWarning("{Title}: the par2 files contain no usable recovery set, skipping verification", job.Title);
            return new VerifyOutcome(null, false, par2Set);
        }

        var candidates = new Par2Candidates(topLevel.Where(f => !par2Set.Contains(f)));
        var results = new List<Par2VerifyResult>();
        for (var i = 0; i < sets.Count; i++)
        {
            var index = i;
            var set = sets[i];
            var message = $"Verifying {set.RecoveryFileIds.Count} file(s)";
            var result = Par2Verifier.Verify(
                work,
                set,
                candidates,
                new InlineProgress<double>(p => progress.Report(new PostProcessProgress(PostProcessStage.Verifying, (index + p) / sets.Count, message))),
                cancellationToken);

            foreach (var renamed in result.Files.Where(f => f.RenamedFrom is not null))
            {
                logger.LogInformation("{Title}: renamed obfuscated file {From} to {To}", job.Title, renamed.RenamedFrom, renamed.Name);
            }

            results.Add(result);
        }

        var damaged = results.Where(r => r.NeedsRepair).ToList();
        if (damaged.Count == 0)
        {
            progress.Report(new PostProcessProgress(PostProcessStage.Verifying, 1, "All files are intact"));
            return new VerifyOutcome(null, false, par2Set);
        }

        // Only now read the recovery data itself, so blocks in damaged .par2 volumes are not counted.
        var verified = Par2Reader.ReadFiles(par2Files, verifyRecoveryData: true, cancellationToken);
        foreach (var result in damaged)
        {
            var available = verified.Sets.GetValueOrDefault(result.Set.SetId)?.RecoveryBlocks ?? 0;
            var broken = string.Join(", ", result.Files.Where(f => f.State != Par2FileState.Ok).Select(f => $"{f.Name} ({f.State.ToString().ToLowerInvariant()}, {f.DamagedSlices} blocks)"));
            logger.LogInformation("{Title}: repair needed for {Files}; {Needed} blocks needed, {Available} available", job.Title, broken, result.BlocksNeeded, available);

            if (result.BlocksNeeded > available)
            {
                return new VerifyOutcome($"Not enough recovery blocks to repair: {result.BlocksNeeded} needed, {available} available.", false, par2Set);
            }
        }

        var par2 = tools.Find(PostProcessTool.Par2);
        if (par2.Path is null)
        {
            return new VerifyOutcome(Par2MissingError, false, par2Set);
        }

        var repairer = new Par2Repairer(runner);
        for (var i = 0; i < damaged.Count; i++)
        {
            var result = damaged[i];
            var index = i;
            var message = $"Repairing {result.BlocksNeeded} block(s)";
            progress.Report(new PostProcessProgress(PostProcessStage.Repairing, (double)i / damaged.Count, message));

            var setFiles = EnsurePar2Extension(result.Set.Par2Files, par2Set);
            var main = setFiles.OrderBy(f => new FileInfo(f).Length).ThenBy(f => f, StringComparer.Ordinal).First();
            var repair = await repairer.RepairAsync(
                par2.Path,
                work,
                main,
                setFiles,
                new InlineProgress<double>(p => progress.Report(new PostProcessProgress(PostProcessStage.Repairing, (index + p) / damaged.Count, message))),
                cancellationToken).ConfigureAwait(false);

            if (!repair.Success)
            {
                return new VerifyOutcome(repair.Error, false, par2Set);
            }
        }

        progress.Report(new PostProcessProgress(PostProcessStage.Repairing, 1, "Repaired"));
        return new VerifyOutcome(null, true, par2Set);
    }

    /// <summary>par2 only picks up recovery files named *.par2; obfuscated ones get the extension back.</summary>
    private static List<string> EnsurePar2Extension(IEnumerable<string> files, HashSet<string> par2Set)
    {
        var result = new List<string>();
        foreach (var file in files)
        {
            if (file.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                result.Add(file);
                continue;
            }

            var renamed = file + ".par2";
            File.Move(file, renamed, overwrite: false);
            par2Set.Remove(file);
            par2Set.Add(renamed);
            result.Add(renamed);
        }

        return result;
    }

    private sealed record ExtractOutcome(string? Error, IReadOnlySet<string> Volumes);

    private async Task<ExtractOutcome> ExtractAsync(PostProcessJob job, string work, string staging, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
            .Where(f => !IsStaging(f) && !f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) && !IsPar2Backup(f) && !Par2Reader.IsPar2File(f))
            .ToList();
        var sets = ArchiveSets.Detect(files);
        var volumes = new HashSet<string>(sets.SelectMany(s => s.Volumes), StringComparer.Ordinal);
        if (sets.Count == 0)
        {
            return new ExtractOutcome(null, volumes);
        }

        if (sets.FirstOrDefault(s => s.FirstVolume is null) is { } incomplete)
        {
            return new ExtractOutcome($"The first volume of {incomplete.Name} is missing.", volumes);
        }

        var sevenZip = tools.Find(PostProcessTool.SevenZip);
        if (sevenZip.Path is null)
        {
            return new ExtractOutcome(SevenZipMissingError, volumes);
        }

        var password = PasswordFor(job);
        var extractor = new SevenZipExtractor(runner);

        for (var i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            var index = i;
            var relative = Path.GetRelativePath(work, Path.GetDirectoryName(set.FirstVolume!)!);
            var output = relative == "." ? staging : Path.Combine(staging, relative);
            var message = sets.Count == 1 ? $"Extracting {set.Name}" : $"Extracting {set.Name} ({i + 1} of {sets.Count})";
            progress.Report(new PostProcessProgress(PostProcessStage.Extracting, (double)i / sets.Count, message));

            var result = await extractor.ExtractAsync(
                sevenZip.Path,
                set.FirstVolume!,
                output,
                password,
                new InlineProgress<double>(p => progress.Report(new PostProcessProgress(PostProcessStage.Extracting, (index + (0.9 * p)) / sets.Count, message))),
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                logger.LogWarning("{Title}: extracting {Archive} failed: {Error}", job.Title, set.Name, result.Error);
                return new ExtractOutcome(result.Error, volumes);
            }
        }

        // One level of nested archives (e.g. a rar inside the rar): extract next to themselves, then drop them.
        var nested = ArchiveSets.Detect(Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories));
        foreach (var set in nested)
        {
            if (set.FirstVolume is null)
            {
                continue;
            }

            progress.Report(new PostProcessProgress(PostProcessStage.Extracting, 0.95, $"Extracting {set.Name}"));
            var result = await extractor.ExtractAsync(sevenZip.Path, set.FirstVolume, Path.GetDirectoryName(set.FirstVolume)!, password, new Progress<double>(), cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return new ExtractOutcome(result.Error, volumes);
            }

            foreach (var volume in set.Volumes)
            {
                File.Delete(volume);
            }
        }

        progress.Report(new PostProcessProgress(PostProcessStage.Extracting, 1, null));
        return new ExtractOutcome(null, volumes);
    }

    /// <summary>Moves extracted files and loose files (no archives, par2, par2 backups, samples or junk) to the target.</summary>
    private List<string> MoveToTarget(string work, string staging, string target, IReadOnlySet<string> volumes, IReadOnlySet<string> par2Files)
    {
        var moves = new List<(string From, string Relative)>();

        if (Directory.Exists(staging))
        {
            moves.AddRange(Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(f => (f, Path.GetRelativePath(staging, f))));
        }

        var loose = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
            .Where(f => !IsStaging(f) && !volumes.Contains(f) && !par2Files.Contains(f) && !IsPar2Backup(f))
            .ToList();
        moves.AddRange(loose.Where(f => !Par2Reader.IsPar2File(f)).Select(f => (f, Path.GetRelativePath(work, f))));

        var moved = new List<string>();
        foreach (var (from, relative) in moves)
        {
            var size = new FileInfo(from).Length;
            if (IsJunk(relative) || IsSample(relative, size))
            {
                logger.LogDebug("Skipping {File}", relative);
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(from, destination, overwrite: true);
            moved.Add(destination);
        }

        moved.Sort(StringComparer.Ordinal);
        return moved;
    }

    public static bool IsJunk(string relativePath) => JunkExtensions.Contains(Path.GetExtension(relativePath));

    /// <summary>"sample" as a word in the file name or a folder name, and smaller than <see cref="SampleMaxBytes"/>.</summary>
    public static bool IsSample(string relativePath, long size) => size < SampleMaxBytes && SampleRegex().IsMatch(relativePath);

    /// <summary>par2 keeps the damaged original as "name.1" next to the repaired "name".</summary>
    private static bool IsPar2Backup(string path)
    {
        var match = Par2BackupRegex().Match(path);
        return match.Success && File.Exists(match.Groups[1].Value);
    }

    private static bool IsStaging(string path) => path.Contains(StagingPrefix, StringComparison.Ordinal);

    private static bool IsSameOrInside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = folder.TrimEnd(Path.DirectorySeparatorChar);
        return path.TrimEnd(Path.DirectorySeparatorChar).Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private void DeleteWorkFolder(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not delete the download folder {Folder}", work);
        }
    }

    private void RemoveStaging(string staging, string target, bool targetExisted)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (!targetExisted && Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
            {
                Directory.Delete(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not remove the temporary folder {Folder}", staging);
        }
    }

    private PostProcessResult Failed(PostProcessJob job, string error)
    {
        logger.LogWarning("Post-processing of {Title} failed: {Error}", job.Title, error);
        return new PostProcessResult(false, error, job.WorkFolder, [], false);
    }

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex TitlePasswordRegex();

    [GeneratedRegex(@"(?<![a-z0-9])samples?(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRegex();

    [GeneratedRegex(@"^(.+)\.\d{1,2}$")]
    private static partial Regex Par2BackupRegex();

    /// <summary>Reports synchronously (System.Progress posts to the thread pool, which reorders updates).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
