// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads;

namespace StreamZH.Library.PostProcessing;

/// <summary>
/// Verify (native par2 reader, with deobfuscation) → repair (external par2, only when needed) → extract (external 7-Zip)
/// → move the wanted files to the target and delete the work folder (unless <see cref="PostProcessJob.KeepFiles"/>).
/// On failure the work folder is kept as it is. <see cref="PostProcessJob.Level"/> stops earlier (SABnzbd's +Repair,
/// +Unpack); the global switches (samples, cleanup list, nested archives, extra tool parameters …) come from
/// <see cref="IPostProcessSwitches"/>.
/// </summary>
public sealed partial class PostProcessor(
    PostProcessTools tools,
    IToolProcessRunner runner,
    ILogger<PostProcessor> logger,
    IPostProcessSwitches? switches = null) : IPostProcessor
{
    /// <summary>Archives inside archives are unpacked this deep when recursive unpacking is on.</summary>
    public const int MaxNestedLevels = 3;

    /// <summary>Files with "sample" in the name below this size are dropped (same idea as Sonarr).</summary>
    public const long SampleMaxBytes = 50L * 1024 * 1024;

    public const string Par2MissingError = "Repair needed but par2 is not installed (Settings → Downloads → Tools)";
    public const string SevenZipMissingError = "Archives found but 7-Zip is not installed (Settings → Downloads → Tools)";

    private const string StagingPrefix = ".streamzh-unpack-";

    /// <summary>
    /// All files below a folder without following symbolic links, junctions or other reparse points: a link from an
    /// archive must never make StreamZH read or move files outside the download.
    /// </summary>
    public static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Win32,
    };

    /// <summary>Never moved to the target, whatever the settings: anything executable (and links to it).</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".bat", ".cmd", ".com", ".pif", ".vbs", ".js", ".jar", ".msi", ".ps1", ".lnk",
    };

    public async Task<PostProcessResult> RunAsync(PostProcessJob job, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);
        var options = (switches ?? FixedPostProcessSwitches.Default).PostProcess;
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
            var warnings = new List<string>();
            var verify = job.Level == PostProcessLevel.None
                ? new VerifyOutcome(null, false, new HashSet<string>(StringComparer.Ordinal))
                : await VerifyAndRepairAsync(job, work, options, progress, cancellationToken).ConfigureAwait(false);
            if (verify.Error is not null)
            {
                if (!job.ContinueWhenUnverified || verify.Error == Par2MissingError)
                {
                    return Failed(job, verify.Error);
                }

                logger.LogWarning("{Title}: not verified ({Error}), unpacking anyway", job.Title, verify.Error);
                warnings.Add($"Not verified: {verify.Error}");
            }

            var extract = job.Level is PostProcessLevel.None or PostProcessLevel.Repair
                ? new ExtractOutcome(null, new HashSet<string>(StringComparer.Ordinal))
                : await ExtractAsync(job, work, staging, options, progress, cancellationToken).ConfigureAwait(false);
            if (extract.Error is not null)
            {
                RemoveStaging(staging, target, targetExisted);
                return Failed(job, extract.Error);
            }

            if (options.UnwantedAction != UnwantedAction.Off && UnwantedFiles(work, staging, options) is { Count: > 0 } unwanted)
            {
                var names = string.Join(", ", unwanted.Take(5));
                if (options.UnwantedAction is UnwantedAction.Pause or UnwantedAction.Fail)
                {
                    RemoveStaging(staging, target, targetExisted);
                    return Failed(job, $"Unwanted files in the download: {names}.");
                }

                logger.LogWarning("{Title}: contains unwanted files: {Files}", job.Title, names);
                warnings.Add($"Unwanted files: {names}.");
            }

            progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 0, "Moving files"));
            var files = MoveToTarget(job, work, staging, target, extract.Volumes, verify.Par2Files, options);
            RemoveStaging(staging, target, targetExisted: true);

            if (files.Count == 0)
            {
                RemoveStaging(staging, target, targetExisted);
                return Failed(job, "No files left to import after post-processing (only samples, .nfo or .par2 files).");
            }

            if (options.Deobfuscate)
            {
                files = DeobfuscateNames(job, target, files);
            }

            if (!job.KeepFiles)
            {
                progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 0.8, "Deleting download folder"));
                DeleteWorkFolder(work);
            }

            progress.Report(new PostProcessProgress(PostProcessStage.Cleaning, 1, null));

            logger.LogInformation("Post-processing of {Title} done: {Count} file(s) in {Target}{Repaired}", job.Title, files.Count, target, verify.Repaired ? " (repaired)" : string.Empty);
            return new PostProcessResult(true, null, target, files, verify.Repaired, warnings.Count > 0 ? string.Join(" ", warnings) : null);
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

    private async Task<VerifyOutcome> VerifyAndRepairAsync(
        PostProcessJob job, string work, PostProcessSwitches options, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
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
                options.Par2Arguments,
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

    private async Task<ExtractOutcome> ExtractAsync(
        PostProcessJob job, string work, string staging, PostProcessSwitches options, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(work, "*", NoLinks)
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
                options.SevenZipArguments,
                new InlineProgress<double>(p => progress.Report(new PostProcessProgress(PostProcessStage.Extracting, (index + (0.9 * p)) / sets.Count, message))),
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                logger.LogWarning("{Title}: extracting {Archive} failed: {Error}", job.Title, set.Name, result.Error);
                return new ExtractOutcome(result.Error, volumes);
            }
        }

        RemoveLinks(job, staging);

        // Nested archives (e.g. a rar inside the rar): extract next to themselves, then drop them. Level by level.
        for (var level = 0; options.RecursiveUnpack && level < MaxNestedLevels && Directory.Exists(staging); level++)
        {
            var nested = ArchiveSets.Detect(Directory.EnumerateFiles(staging, "*", NoLinks)).Where(s => s.FirstVolume is not null).ToList();
            if (nested.Count == 0)
            {
                break;
            }

            foreach (var set in nested)
            {
                progress.Report(new PostProcessProgress(PostProcessStage.Extracting, 0.95, $"Extracting {set.Name}"));
                var result = await extractor.ExtractAsync(
                        sevenZip.Path, set.FirstVolume!, Path.GetDirectoryName(set.FirstVolume)!, password, options.SevenZipArguments, new Progress<double>(), cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    return new ExtractOutcome(result.Error, volumes);
                }

                foreach (var volume in set.Volumes)
                {
                    File.Delete(volume);
                }
            }

            RemoveLinks(job, staging);
        }

        progress.Report(new PostProcessProgress(PostProcessStage.Extracting, 1, null));
        return new ExtractOutcome(null, volumes);
    }

    /// <summary>
    /// Deletes symbolic links, junctions and other reparse points that an archive created below <paramref name="folder"/>
    /// (the link only, never what it points to). Older 7-Zip versions create them from crafted archives.
    /// </summary>
    private void RemoveLinks(PostProcessJob job, string folder)
    {
        var removed = DeleteLinks(folder);
        if (removed > 0)
        {
            logger.LogWarning("{Title}: removed {Count} symbolic link(s) the archive contained", job.Title, removed);
        }
    }

    /// <summary>Removes every link below <paramref name="folder"/> without following any; the number removed.</summary>
    public static int DeleteLinks(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var removed = 0;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo && OperatingSystem.IsWindows())
                {
                    Directory.Delete(entry.FullName); // removes the junction or directory link itself
                }
                else
                {
                    File.Delete(entry.FullName); // unlink: also a link to a directory on Linux and macOS
                }

                removed++;
            }
            else if (entry is DirectoryInfo directory)
            {
                removed += DeleteLinks(directory.FullName);
            }
        }

        return removed;
    }

    /// <summary>
    /// Moves extracted files and loose files to the target. Full post-processing leaves out archives, par2 files, par2
    /// backups, samples and the cleanup list; the lower levels move everything but executables and par2 backups.
    /// </summary>
    private List<string> MoveToTarget(
        PostProcessJob job, string work, string staging, string target, IReadOnlySet<string> volumes, IReadOnlySet<string> par2Files, PostProcessSwitches options)
    {
        var moves = new List<(string From, string Relative)>();
        var full = job.Level == PostProcessLevel.Full;

        if (Directory.Exists(staging))
        {
            moves.AddRange(Directory.EnumerateFiles(staging, "*", NoLinks).Select(f => (f, Path.GetRelativePath(staging, f))));
        }

        var loose = Directory.EnumerateFiles(work, "*", NoLinks)
            .Where(f => !IsStaging(f) && !IsPar2Backup(f) && (!full || (!volumes.Contains(f) && !par2Files.Contains(f) && !Par2Reader.IsPar2File(f))))
            .ToList();
        moves.AddRange(loose.Select(f => (f, Path.GetRelativePath(work, f))));

        var moved = new List<string>();
        foreach (var (from, relative) in moves)
        {
            var size = new FileInfo(from).Length;
            if (IsExecutable(relative)
                || (full && (options.CleanupExtensions.Contains(Path.GetExtension(relative)) || IsPar2Extension(relative)))
                || (options.IgnoreSamples && job.Level != PostProcessLevel.None && IsSample(relative, size)))
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

    /// <summary>Left out with the default settings: executables, par2 files and the default cleanup list (release info, checksums, index files).</summary>
    public static bool IsJunk(string relativePath) =>
        IsExecutable(relativePath) || IsPar2Extension(relativePath)
        || Search.DownloadSettings.DefaultCleanupList.Contains(Path.GetExtension(relativePath).ToLowerInvariant());

    public static bool IsExecutable(string relativePath) => ExecutableExtensions.Contains(Path.GetExtension(relativePath));

    /// <summary>"a3f9c2…" style names: no separators, at least 16 characters, letters and digits mixed.</summary>
    public static bool IsObfuscated(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length >= 16 && ObfuscatedRegex().IsMatch(stem) && stem.Any(char.IsDigit) && stem.Any(char.IsLetter);
    }

    private static bool IsPar2Extension(string relativePath) => Path.GetExtension(relativePath).Equals(".par2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Names of files (downloaded or unpacked) with an unwanted extension.</summary>
    private static List<string> UnwantedFiles(string work, string staging, PostProcessSwitches options)
    {
        if (options.UnwantedExtensions.Count == 0)
        {
            return [];
        }

        IEnumerable<string> files = Directory.EnumerateFiles(work, "*", NoLinks).Where(f => !IsStaging(f));
        if (Directory.Exists(staging))
        {
            files = files.Concat(Directory.EnumerateFiles(staging, "*", NoLinks));
        }

        return [.. files.Where(f => options.UnwantedExtensions.Contains(Path.GetExtension(f))).Select(f => Path.GetFileName(f)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// SABnzbd's "Deobfuscate final filenames": when the biggest file has a random name, it and every file with the same
    /// stem (subtitles, nfo) get the download's name. Only files directly in the target are renamed.
    /// </summary>
    private List<string> DeobfuscateNames(PostProcessJob job, string target, List<string> files)
    {
        var top = files.Where(f => string.Equals(Path.GetDirectoryName(f), target, StringComparison.Ordinal)).ToList();
        var biggest = top.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        if (biggest is null || !IsObfuscated(Path.GetFileName(biggest)))
        {
            return files;
        }

        var stem = Path.GetFileNameWithoutExtension(biggest);
        var title = Search.NzbFileNames.SafeName(job.Title);
        var result = new List<string>(files);
        foreach (var file in top.Where(f => Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal)))
        {
            var renamed = Path.Combine(target, title + Path.GetFileName(file)[stem.Length..]);
            if (File.Exists(renamed))
            {
                continue;
            }

            File.Move(file, renamed);
            result[result.IndexOf(file)] = renamed;
            logger.LogInformation("{Title}: renamed obfuscated file {From} to {To}", job.Title, Path.GetFileName(file), Path.GetFileName(renamed));
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

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

    [GeneratedRegex(@"^[A-Za-z0-9]+$")]
    private static partial Regex ObfuscatedRegex();

    /// <summary>Reports synchronously (System.Progress posts to the thread pool, which reorders updates).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
