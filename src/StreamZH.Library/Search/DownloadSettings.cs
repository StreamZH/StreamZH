// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Library.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Library.Search;

/// <summary>Where grabbed releases go: the built-in download engine, or an NZB folder another downloader watches.</summary>
public enum DownloadClientKind
{
    BuiltIn,
    Blackhole,
}

/// <summary>Fields to change; null keeps the current value, an empty folder means the default (or "off" for optional folders).</summary>
public sealed record DownloadSettingsChange
{
    public string? Client { get; init; }
    public string? NzbFolder { get; init; }
    public string? IncompleteFolder { get; init; }
    public string? CompleteFolder { get; init; }
    public long? SpeedLimitKBps { get; init; }
    public double? MinFreeSpaceGb { get; init; }
    public bool? KeepFiles { get; init; }
    public bool? AbortHopeless { get; init; }
    public int? ArticleTries { get; init; }
    public string? NzbBackupFolder { get; init; }
    public string? WatchFolder { get; init; }
    public int? WatchIntervalSeconds { get; init; }
    public bool? PauseOnStart { get; init; }
    public bool? PreCheck { get; init; }
    public int? MaxActiveDownloads { get; init; }
    public int? PropagationDelayMinutes { get; init; }
    public string? DuplicateAction { get; init; }
    public string? AutoSort { get; init; }
    public string? UnwantedExtensions { get; init; }
    public string? UnwantedAction { get; init; }
    public string? EncryptedAction { get; init; }
    public bool? DownloadAllPar2 { get; init; }
    public bool? OnlyVerified { get; init; }
    public bool? RecursiveUnpack { get; init; }
    public bool? IgnoreSamples { get; init; }
    public string? CleanupList { get; init; }
    public bool? Deobfuscate { get; init; }
    public bool? TemporaryFolderName { get; init; }
    public string? Par2Arguments { get; init; }
    public string? SevenZipArguments { get; init; }
    public string? ToolPriority { get; init; }
    public int? PostProcessWorkers { get; init; }
    public long? MaxLineSpeedKBps { get; init; }
    public int? HistoryKeepDays { get; init; }
    public int? HistoryKeepItems { get; init; }
    public bool? HistoryKeepFailed { get; init; }
}

/// <summary>
/// Download settings: which client gets grabbed releases, the NZB folder (blackhole), the folders of the built-in engine,
/// its speed limit and the SABnzbd-style switches for the queue, post-processing, history and special cases. Values are
/// cached (the engine reads them on every tick and article) and re-read after <see cref="Update(DownloadSettingsChange)"/>.
/// </summary>
public sealed class DownloadSettings(ConfigRepository config, AppPaths paths) : IPostProcessSwitches
{
    public const string BuiltInValue = "builtin";
    public const string BlackholeValue = "blackhole";

    /// <summary>10 GB/s; anything above is a typo.</summary>
    public const long MaxSpeedLimitKBps = 10_000_000;

    /// <summary>Below this much free space in the incomplete folder the queue pauses (unless set otherwise).</summary>
    public const double DefaultMinFreeSpaceGb = 1;

    public const double MaxMinFreeSpaceGb = 10_000;

    /// <summary>Tries per server and article on timeouts and dropped connections (like SABnzbd's "max_art_tries").</summary>
    public const int DefaultArticleTries = 3;

    public const int MaxArticleTries = 10;

    /// <summary>The watched folder is scanned this often (SABnzbd's "dirscan_speed").</summary>
    public const int DefaultWatchIntervalSeconds = 5;

    public const int MaxWatchIntervalSeconds = 3600;
    public const int MaxActiveDownloadsLimit = 100;
    public const int MaxPropagationDelayMinutes = 1440;
    public const int MaxPostProcessWorkers = 4;
    public const int MaxHistoryDays = 3650;
    public const int MaxHistoryItems = 100_000;

    /// <summary>Deleted after unpacking (never moved to the completed folder) unless the list is changed.</summary>
    public static readonly IReadOnlyList<string> DefaultCleanupList = [".nfo", ".sfv", ".srr", ".srs", ".nzb", ".md5", ".url", ".db"];

    /// <summary>The suggested unwanted extensions (the action is off by default).</summary>
    public static readonly IReadOnlyList<string> DefaultUnwantedExtensions =
        [".exe", ".scr", ".bat", ".cmd", ".com", ".pif", ".vbs", ".js", ".jar", ".msi", ".ps1", ".lnk"];

    private const long BytesPerGb = 1024L * 1024 * 1024;

    private long? _speedLimit;
    private Values? _values;

    /// <summary>Everything the engine and post-processing read, parsed once.</summary>
    private sealed record Values(
        double MinFreeSpaceGb,
        bool KeepFiles,
        bool AbortHopeless,
        int ArticleTries,
        string? NzbBackupFolder,
        string? WatchFolder,
        int WatchIntervalSeconds,
        bool PauseOnStart,
        bool PreCheck,
        int MaxActiveDownloads,
        int PropagationDelayMinutes,
        DuplicateAction DuplicateAction,
        QueueSort AutoSort,
        IReadOnlyList<string> UnwantedExtensions,
        UnwantedAction UnwantedAction,
        EncryptedAction EncryptedAction,
        bool DownloadAllPar2,
        bool OnlyVerified,
        bool RecursiveUnpack,
        bool IgnoreSamples,
        IReadOnlyList<string> CleanupList,
        bool Deobfuscate,
        bool TemporaryFolderName,
        IReadOnlyList<string> Par2Arguments,
        IReadOnlyList<string> SevenZipArguments,
        ToolPriority ToolPriority,
        int PostProcessWorkers,
        long MaxLineSpeedKBps,
        int HistoryKeepDays,
        int HistoryKeepItems,
        bool HistoryKeepFailed);

    public string DefaultNzbFolder => Path.Combine(paths.DataDirectory, "downloads", "nzb");

    public string DefaultIncompleteFolder => paths.IncompleteDirectory;

    public string DefaultCompleteFolder => Path.Combine(paths.DataDirectory, "downloads", "complete");

    /// <summary>The engine keeps a copy of every queued NZB here as "{id}.nzb" (for resume and retry).</summary>
    public string NzbQueueFolder => Path.Combine(paths.DataDirectory, "downloads", "nzb-queue");

    public string NzbFolder => Usable(config.Get(ConfigKeys.NzbFolder)) ?? DefaultNzbFolder;

    public string IncompleteFolder => Usable(config.Get(ConfigKeys.IncompleteFolder)) ?? DefaultIncompleteFolder;

    public string CompleteFolder => Usable(config.Get(ConfigKeys.CompleteFolder)) ?? DefaultCompleteFolder;

    /// <summary>
    /// 0.12 (V-02): why a folder may not hold downloads (inside the data folder outside downloads/, or containing it), null
    /// when it may. Used for every download, category, watch and torrent folder, on save and when it is used.
    /// </summary>
    public string? FolderProblem(string? folder) => paths.FolderProblem(folder);

    /// <summary>The data folder (for the checks of other folder settings).</summary>
    public AppPaths Paths => paths;

    // A stored folder that is empty or not allowed (older version, restored database): the default is used instead.
    private string? Usable(string? stored) => stored is { Length: > 0 } && FolderProblem(stored) is null ? stored : null;

    /// <summary>The built-in engine unless the blackhole folder was chosen.</summary>
    public DownloadClientKind Client =>
        string.Equals(config.Get(ConfigKeys.DownloadClient), BlackholeValue, StringComparison.OrdinalIgnoreCase)
            ? DownloadClientKind.Blackhole
            : DownloadClientKind.BuiltIn;

    /// <summary>Global download speed limit in KB/s; 0 = unlimited. Cached, read on every article.</summary>
    public long SpeedLimitKBps
    {
        get
        {
            if (_speedLimit is { } cached)
            {
                return cached;
            }

            var value = long.TryParse(config.Get(ConfigKeys.DownloadSpeedLimit), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            _speedLimit = value;
            return value;
        }
    }

    /// <summary>The queue pauses below this much free space in the incomplete folder (GB of 1024³ bytes, like the disk notice).</summary>
    public double MinFreeSpaceGb => Current.MinFreeSpaceGb;

    public long MinFreeSpaceBytes => (long)(MinFreeSpaceGb * BytesPerGb);

    /// <summary>After a successful unpack the download folder (par2, archives, nfo) stays in the incomplete folder instead of being deleted.</summary>
    public bool KeepFiles => Current.KeepFiles;

    /// <summary>A download fails as soon as more articles are missing than par2 could repair (default), instead of downloading the rest.</summary>
    public bool AbortHopeless => Current.AbortHopeless;

    /// <summary>Tries per server and article on timeouts and dropped connections.</summary>
    public int ArticleTries => Current.ArticleTries;

    /// <summary>A copy of every added NZB goes here; null = off.</summary>
    public string? NzbBackupFolder => Current.NzbBackupFolder;

    /// <summary>NZB files dropped here are added to the queue; null = off.</summary>
    public string? WatchFolder => Current.WatchFolder;

    public TimeSpan WatchInterval => TimeSpan.FromSeconds(Current.WatchIntervalSeconds);

    /// <summary>The queue starts paused after every start of StreamZH.</summary>
    public bool PauseOnStart => Current.PauseOnStart;

    /// <summary>Check with STAT that the articles exist before downloading.</summary>
    public bool PreCheck => Current.PreCheck;

    /// <summary>Downloads that get articles at the same time; 0 = no limit (the next starts as soon as the one before has no unassigned articles).</summary>
    public int MaxActiveDownloads => Current.MaxActiveDownloads;

    /// <summary>A release is only downloaded this long after it was posted.</summary>
    public TimeSpan PropagationDelay => TimeSpan.FromMinutes(Current.PropagationDelayMinutes);

    public DuplicateAction DuplicateAction => Current.DuplicateAction;

    public QueueSort AutoSort => Current.AutoSort;

    public IReadOnlyList<string> UnwantedExtensions => Current.UnwantedExtensions;

    public UnwantedAction UnwantedAction => Current.UnwantedAction;

    public EncryptedAction EncryptedAction => Current.EncryptedAction;

    /// <summary>Fetch all par2 recovery volumes right away instead of only what a repair needs.</summary>
    public bool DownloadAllPar2 => Current.DownloadAllPar2;

    /// <summary>Only unpack and complete downloads that par2 verified (or repaired). Off: unpack anyway.</summary>
    public bool OnlyVerified => Current.OnlyVerified;

    /// <summary>Post-processing writes into "_UNPACK_&lt;name&gt;" and renames the folder when it is done.</summary>
    public bool TemporaryFolderName => Current.TemporaryFolderName;

    public ToolPriority ToolPriority => Current.ToolPriority;

    public int PostProcessWorkers => Current.PostProcessWorkers;

    /// <summary>The speed of the internet line in KB/s, for speed limits in percent; 0 = not set.</summary>
    public long MaxLineSpeedKBps => Current.MaxLineSpeedKBps;

    public int HistoryKeepDays => Current.HistoryKeepDays;

    public int HistoryKeepItems => Current.HistoryKeepItems;

    public bool HistoryKeepFailed => Current.HistoryKeepFailed;

    public string CleanupListText => ExtensionList.Format(Current.CleanupList);

    public string UnwantedExtensionsText => ExtensionList.Format(Current.UnwantedExtensions);

    public string Par2ArgumentsText => string.Join(' ', Current.Par2Arguments.Select(Quote));

    public string SevenZipArgumentsText => string.Join(' ', Current.SevenZipArguments.Select(Quote));

    /// <summary>What the post-processor and the tool runner read.</summary>
    public PostProcessSwitches PostProcess
    {
        get
        {
            var v = Current;
            return new PostProcessSwitches(
                v.RecursiveUnpack,
                v.IgnoreSamples,
                new HashSet<string>(v.CleanupList, StringComparer.OrdinalIgnoreCase),
                v.Deobfuscate,
                new HashSet<string>(v.UnwantedExtensions, StringComparer.OrdinalIgnoreCase),
                v.UnwantedAction,
                v.Par2Arguments,
                v.SevenZipArguments,
                v.ToolPriority);
        }
    }

    private Values Current => _values ??= Load();

    public static string ClientValue(DownloadClientKind kind) => kind == DownloadClientKind.Blackhole ? BlackholeValue : BuiltInValue;

    /// <summary>Saves the NZB folder (empty = default). Creates it when missing and checks that StreamZH can write there.</summary>
    /// <exception cref="ValidationFailedException">The path is not a full path or not writable.</exception>
    public string SetNzbFolder(string? path)
    {
        var errors = new Dictionary<string, string[]>();
        var full = ValidateFolder(path, DefaultNzbFolder, "nzbFolder", "/downloads/nzb or D:\\Downloads\\nzb", errors);
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        config.Set(ConfigKeys.NzbFolder, PathComparer.Instance.Equals(full, DefaultNzbFolder) ? string.Empty : full);
        return full;
    }

    /// <summary>Sets the speed limit (the scheduler, the queue's speed menu). 0 = unlimited.</summary>
    /// <exception cref="ValidationFailedException">Out of range.</exception>
    public void SetSpeedLimit(long kbps) => Update(new DownloadSettingsChange { SpeedLimitKBps = kbps });

    /// <summary>Saves the given settings (see <see cref="Update(DownloadSettingsChange)"/>).</summary>
    public void Update(
        string? client,
        string? nzbFolder,
        string? incompleteFolder,
        string? completeFolder,
        long? speedLimitKBps,
        double? minFreeSpaceGb = null,
        bool? keepFiles = null,
        bool? abortHopeless = null,
        int? articleTries = null) => Update(new DownloadSettingsChange
        {
            Client = client,
            NzbFolder = nzbFolder,
            IncompleteFolder = incompleteFolder,
            CompleteFolder = completeFolder,
            SpeedLimitKBps = speedLimitKBps,
            MinFreeSpaceGb = minFreeSpaceGb,
            KeepFiles = keepFiles,
            AbortHopeless = abortHopeless,
            ArticleTries = articleTries,
        });

    /// <summary>
    /// Saves the given settings; null keeps the current value. All fields are checked first and nothing is saved when
    /// one is invalid.
    /// </summary>
    /// <exception cref="ValidationFailedException">One or more fields are invalid (keys are the camelCase field names).</exception>
    public void Update(DownloadSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var errors = new Dictionary<string, string[]>();
        var set = new List<(string Key, string Value)>();

        if (change.Client is { } client)
        {
            DownloadClientKind? kind = client.Trim().ToLowerInvariant() switch
            {
                BuiltInValue => DownloadClientKind.BuiltIn,
                BlackholeValue => DownloadClientKind.Blackhole,
                _ => null,
            };
            if (kind is { } k)
            {
                set.Add((ConfigKeys.DownloadClient, ClientValue(k)));
            }
            else
            {
                errors["client"] = [$"Use {BuiltInValue} or {BlackholeValue}."];
            }
        }

        var nzb = change.NzbFolder is null ? null : ValidateFolder(change.NzbFolder, DefaultNzbFolder, "nzbFolder", "/downloads/nzb or D:\\Downloads\\nzb", errors);
        var incomplete = change.IncompleteFolder is null
            ? null
            : ValidateFolder(change.IncompleteFolder, DefaultIncompleteFolder, "incompleteFolder", "/downloads/incomplete or D:\\Downloads\\incomplete", errors);
        var complete = change.CompleteFolder is null
            ? null
            : ValidateFolder(change.CompleteFolder, DefaultCompleteFolder, "completeFolder", "/downloads/complete or D:\\Downloads\\complete", errors);

        if (!errors.ContainsKey("incompleteFolder") && !errors.ContainsKey("completeFolder")
            && PathComparer.Instance.Equals(incomplete ?? IncompleteFolder, complete ?? CompleteFolder))
        {
            errors["completeFolder"] = ["Use a different folder than for incomplete downloads."];
        }

        if (nzb is not null && !errors.ContainsKey("nzbFolder"))
        {
            set.Add((ConfigKeys.NzbFolder, PathComparer.Instance.Equals(nzb, DefaultNzbFolder) ? string.Empty : nzb));
        }

        if (incomplete is not null && !errors.ContainsKey("incompleteFolder"))
        {
            set.Add((ConfigKeys.IncompleteFolder, PathComparer.Instance.Equals(incomplete, DefaultIncompleteFolder) ? string.Empty : incomplete));
        }

        if (complete is not null && !errors.ContainsKey("completeFolder"))
        {
            set.Add((ConfigKeys.CompleteFolder, PathComparer.Instance.Equals(complete, DefaultCompleteFolder) ? string.Empty : complete));
        }

        // Optional folders: empty switches them off.
        var backup = change.NzbBackupFolder is null ? null : ValidateOptionalFolder(change.NzbBackupFolder, "nzbBackupFolder", errors);
        var watch = change.WatchFolder is null ? null : ValidateOptionalFolder(change.WatchFolder, "watchFolder", errors);
        if (watch is { Length: > 0 } && !errors.ContainsKey("watchFolder"))
        {
            var taken = new[] { nzb ?? NzbFolder, incomplete ?? IncompleteFolder, complete ?? CompleteFolder, NzbQueueFolder, backup ?? NzbBackupFolder };
            if (taken.Any(f => f is not null && PathComparer.Instance.Equals(f, watch)))
            {
                errors["watchFolder"] = ["Use a folder of its own; StreamZH removes the NZB files it picks up there."];
            }
        }

        if (backup is not null && !errors.ContainsKey("nzbBackupFolder"))
        {
            set.Add((ConfigKeys.DownloadNzbBackupFolder, backup));
        }

        if (watch is not null && !errors.ContainsKey("watchFolder"))
        {
            set.Add((ConfigKeys.DownloadWatchFolder, watch));
        }

        if (change.SpeedLimitKBps is { } limit)
        {
            if (limit is < 0 or > MaxSpeedLimitKBps)
            {
                errors["speedLimitKBps"] = [$"Use 0 (unlimited) to {MaxSpeedLimitKBps.ToString("N0", CultureInfo.InvariantCulture)} KB/s."];
            }
            else
            {
                set.Add((ConfigKeys.DownloadSpeedLimit, limit.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (change.MaxLineSpeedKBps is { } line)
        {
            if (line is < 0 or > MaxSpeedLimitKBps)
            {
                errors["maxLineSpeedKBps"] = [$"Use 0 (not set) to {MaxSpeedLimitKBps.ToString("N0", CultureInfo.InvariantCulture)} KB/s."];
            }
            else
            {
                set.Add((ConfigKeys.DownloadMaxLineSpeed, line.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (change.MinFreeSpaceGb is { } gb)
        {
            if (!double.IsFinite(gb) || gb < 0 || gb > MaxMinFreeSpaceGb)
            {
                errors["minFreeSpaceGb"] = [$"Use 0 (never pause) to {MaxMinFreeSpaceGb.ToString("N0", CultureInfo.InvariantCulture)} GB."];
            }
            else
            {
                set.Add((ConfigKeys.DownloadMinFreeSpaceGb, Math.Round(gb, 2).ToString(CultureInfo.InvariantCulture)));
            }
        }

        Number(change.ArticleTries, 1, MaxArticleTries, "articleTries", ConfigKeys.DownloadArticleTries, "Use 1 to {1} tries.");
        Number(change.WatchIntervalSeconds, 1, MaxWatchIntervalSeconds, "watchIntervalSeconds", ConfigKeys.DownloadWatchInterval, "Use 1 to {1} seconds.");
        Number(change.MaxActiveDownloads, 0, MaxActiveDownloadsLimit, "maxActiveDownloads", ConfigKeys.DownloadMaxActive, "Use 0 (no limit) to {1}.");
        Number(change.PropagationDelayMinutes, 0, MaxPropagationDelayMinutes, "propagationDelayMinutes", ConfigKeys.DownloadPropagationDelay, "Use 0 (off) to {1} minutes.");
        Number(change.PostProcessWorkers, 1, MaxPostProcessWorkers, "postProcessWorkers", ConfigKeys.DownloadPostProcessWorkers, "Use 1 to {1}.");
        Number(change.HistoryKeepDays, 0, MaxHistoryDays, "historyKeepDays", ConfigKeys.DownloadHistoryKeepDays, "Use 0 (keep all) to {1} days.");
        Number(change.HistoryKeepItems, 0, MaxHistoryItems, "historyKeepItems", ConfigKeys.DownloadHistoryKeepItems, "Use 0 (keep all) to {1}.");

        Switch(change.KeepFiles, ConfigKeys.DownloadKeepFiles);
        Switch(change.AbortHopeless, ConfigKeys.DownloadAbortHopeless);
        Switch(change.PauseOnStart, ConfigKeys.DownloadPauseOnStart);
        Switch(change.PreCheck, ConfigKeys.DownloadPreCheck);
        Switch(change.DownloadAllPar2, ConfigKeys.DownloadAllPar2);
        Switch(change.OnlyVerified, ConfigKeys.DownloadOnlyVerified);
        Switch(change.RecursiveUnpack, ConfigKeys.DownloadRecursiveUnpack);
        Switch(change.IgnoreSamples, ConfigKeys.DownloadIgnoreSamples);
        Switch(change.Deobfuscate, ConfigKeys.DownloadDeobfuscate);
        Switch(change.TemporaryFolderName, ConfigKeys.DownloadTemporaryFolderName);
        Switch(change.HistoryKeepFailed, ConfigKeys.DownloadHistoryKeepFailed);

        Choice<DuplicateAction>(change.DuplicateAction, "duplicateAction", ConfigKeys.DownloadDuplicateAction);
        Choice<QueueSort>(change.AutoSort, "autoSort", ConfigKeys.DownloadAutoSort);
        Choice<UnwantedAction>(change.UnwantedAction, "unwantedAction", ConfigKeys.DownloadUnwantedAction);
        Choice<EncryptedAction>(change.EncryptedAction, "encryptedAction", ConfigKeys.DownloadEncryptedAction);
        Choice<ToolPriority>(change.ToolPriority, "toolPriority", ConfigKeys.DownloadToolPriority);

        Extensions(change.UnwantedExtensions, "unwantedExtensions", ConfigKeys.DownloadUnwantedExtensions);
        Extensions(change.CleanupList, "cleanupList", ConfigKeys.DownloadCleanupList);
        Arguments(change.Par2Arguments, sevenZip: false, "par2Arguments", ConfigKeys.DownloadPar2Arguments);
        Arguments(change.SevenZipArguments, sevenZip: true, "sevenZipArguments", ConfigKeys.DownloadSevenZipArguments);

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        foreach (var (key, value) in set)
        {
            config.Set(key, value);
        }

        _speedLimit = null;
        _values = null;

        void Number(int? value, int min, int max, string field, string key, string message)
        {
            if (value is not { } n)
            {
                return;
            }

            if (n < min || n > max)
            {
                errors[field] = [string.Format(CultureInfo.InvariantCulture, message, min, max.ToString("N0", CultureInfo.InvariantCulture))];
                return;
            }

            set.Add((key, n.ToString(CultureInfo.InvariantCulture)));
        }

        void Switch(bool? value, string key)
        {
            if (value is { } on)
            {
                set.Add((key, on ? "true" : "false"));
            }
        }

        void Choice<T>(string? value, string field, string key)
            where T : struct, Enum
        {
            if (value is null)
            {
                return;
            }

            if (DownloadOptionValues.Parse<T>(value) is { } parsed)
            {
                set.Add((key, DownloadOptionValues.Of(parsed)));
            }
            else
            {
                errors[field] = [$"Use one of: {DownloadOptionValues.Names<T>()}."];
            }
        }

        void Extensions(string? value, string field, string key)
        {
            if (value is null)
            {
                return;
            }

            if (ExtensionList.Parse(value, out var invalid) is { } list)
            {
                set.Add((key, ExtensionList.Format(list)));
            }
            else
            {
                errors[field] = [$"\"{invalid}\" is not a file extension. Separate extensions with commas, e.g. .nfo, .url."];
            }
        }

        void Arguments(string? value, bool sevenZip, string field, string key)
        {
            if (value is null)
            {
                return;
            }

            if (ToolArguments.Parse(value, sevenZip, out var problem) is { } arguments)
            {
                set.Add((key, string.Join('\n', arguments)));
            }
            else
            {
                errors[field] = [problem!];
            }
        }
    }

    /// <summary>Null when the folder exists (or could be created) and is writable, otherwise the reason.</summary>
    public static string? CheckWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".streamzh-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"StreamZH cannot write to this folder: {ex.Message}";
        }
    }

    private Values Load()
    {
        var minFree = double.TryParse(config.Get(ConfigKeys.DownloadMinFreeSpaceGb), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb)
            && double.IsFinite(gb) && gb >= 0
            ? gb
            : DefaultMinFreeSpaceGb;
        return new Values(
            minFree,
            Flag(ConfigKeys.DownloadKeepFiles, false),
            Flag(ConfigKeys.DownloadAbortHopeless, true),
            Int(ConfigKeys.DownloadArticleTries, 1, MaxArticleTries, DefaultArticleTries),
            Usable(config.Get(ConfigKeys.DownloadNzbBackupFolder)),
            Usable(config.Get(ConfigKeys.DownloadWatchFolder)),
            Int(ConfigKeys.DownloadWatchInterval, 1, MaxWatchIntervalSeconds, DefaultWatchIntervalSeconds),
            Flag(ConfigKeys.DownloadPauseOnStart, false),
            Flag(ConfigKeys.DownloadPreCheck, false),
            Int(ConfigKeys.DownloadMaxActive, 0, MaxActiveDownloadsLimit, 0),
            Int(ConfigKeys.DownloadPropagationDelay, 0, MaxPropagationDelayMinutes, 0),
            DownloadOptionValues.Parse<DuplicateAction>(config.Get(ConfigKeys.DownloadDuplicateAction)) ?? DuplicateAction.Off,
            DownloadOptionValues.Parse<QueueSort>(config.Get(ConfigKeys.DownloadAutoSort)) ?? QueueSort.Off,
            ExtensionList.Parse(config.Get(ConfigKeys.DownloadUnwantedExtensions) ?? ExtensionList.Format(DefaultUnwantedExtensions), out _) ?? DefaultUnwantedExtensions,
            DownloadOptionValues.Parse<UnwantedAction>(config.Get(ConfigKeys.DownloadUnwantedAction)) ?? UnwantedAction.Off,
            DownloadOptionValues.Parse<EncryptedAction>(config.Get(ConfigKeys.DownloadEncryptedAction)) ?? EncryptedAction.Off,
            Flag(ConfigKeys.DownloadAllPar2, false),
            Flag(ConfigKeys.DownloadOnlyVerified, true),
            Flag(ConfigKeys.DownloadRecursiveUnpack, true),
            Flag(ConfigKeys.DownloadIgnoreSamples, true),
            ExtensionList.Parse(config.Get(ConfigKeys.DownloadCleanupList) ?? ExtensionList.Format(DefaultCleanupList), out _) ?? DefaultCleanupList,
            Flag(ConfigKeys.DownloadDeobfuscate, false),
            Flag(ConfigKeys.DownloadTemporaryFolderName, false),
            StoredArguments(ConfigKeys.DownloadPar2Arguments, sevenZip: false),
            StoredArguments(ConfigKeys.DownloadSevenZipArguments, sevenZip: true),
            DownloadOptionValues.Parse<ToolPriority>(config.Get(ConfigKeys.DownloadToolPriority)) ?? ToolPriority.Normal,
            Int(ConfigKeys.DownloadPostProcessWorkers, 1, MaxPostProcessWorkers, 1),
            long.TryParse(config.Get(ConfigKeys.DownloadMaxLineSpeed), NumberStyles.None, CultureInfo.InvariantCulture, out var lineSpeed) ? lineSpeed : 0,
            Int(ConfigKeys.DownloadHistoryKeepDays, 0, MaxHistoryDays, 0),
            Int(ConfigKeys.DownloadHistoryKeepItems, 0, MaxHistoryItems, 0),
            Flag(ConfigKeys.DownloadHistoryKeepFailed, true));
    }

    private bool Flag(string key, bool fallback)
    {
        var value = config.Get(key);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || (fallback && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
    }

    private int Int(string key, int min, int max, int fallback) =>
        int.TryParse(config.Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : fallback;

    // 0.12: values stored by an earlier version that are not on the allow-list are dropped.
    private IReadOnlyList<string> StoredArguments(string key, bool sevenZip) =>
        config.Get(key) is { Length: > 0 } stored
            ? [.. stored.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(a => ToolArguments.IsAllowed(a, sevenZip)).Take(ToolArguments.MaxArguments)]
            : [];

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    private string ValidateFolder(string? path, string defaultFolder, string field, string example, Dictionary<string, string[]> errors)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && !Path.IsPathFullyQualified(trimmed))
        {
            errors[field] = [$"Enter a full path, e.g. {example}."];
            return trimmed;
        }

        var full = trimmed.Length == 0 ? defaultFolder : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (FolderProblem(full) is { } dataProblem)
        {
            errors[field] = [dataProblem];
        }
        else if (CheckWritable(full) is { } problem)
        {
            errors[field] = [problem];
        }

        return full;
    }

    /// <summary>"" (off) or a full, writable path.</summary>
    private string ValidateOptionalFolder(string path, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            errors[field] = ["Enter a full path, e.g. /downloads/watch or D:\\Downloads\\watch, or leave it empty to switch it off."];
            return trimmed;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (FolderProblem(full) is { } dataProblem)
        {
            errors[field] = [dataProblem];
        }
        else if (CheckWritable(full) is { } problem)
        {
            errors[field] = [problem];
        }

        return full;
    }
}
