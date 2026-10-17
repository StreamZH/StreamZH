// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>Where grabbed releases go: the built-in download engine, or an NZB folder another downloader watches.</summary>
public enum DownloadClientKind
{
    BuiltIn,
    Blackhole,
}

/// <summary>
/// Download settings: which client gets grabbed releases, the NZB folder (blackhole), the incomplete and complete
/// folders of the built-in engine and its speed limit.
/// </summary>
public sealed class DownloadSettings(ConfigRepository config, AppPaths paths)
{
    public const string BuiltInValue = "builtin";
    public const string BlackholeValue = "blackhole";

    /// <summary>10 GB/s; anything above is a typo.</summary>
    public const long MaxSpeedLimitKBps = 10_000_000;

    private long? _speedLimit;

    public string DefaultNzbFolder => Path.Combine(paths.DataDirectory, "downloads", "nzb");

    public string DefaultIncompleteFolder => paths.IncompleteDirectory;

    public string DefaultCompleteFolder => Path.Combine(paths.DataDirectory, "downloads", "complete");

    /// <summary>The engine keeps a copy of every queued NZB here as "{id}.nzb" (for resume and retry).</summary>
    public string NzbQueueFolder => Path.Combine(paths.DataDirectory, "downloads", "nzb-queue");

    public string NzbFolder => config.Get(ConfigKeys.NzbFolder) is { Length: > 0 } folder ? folder : DefaultNzbFolder;

    public string IncompleteFolder => config.Get(ConfigKeys.IncompleteFolder) is { Length: > 0 } folder ? folder : DefaultIncompleteFolder;

    public string CompleteFolder => config.Get(ConfigKeys.CompleteFolder) is { Length: > 0 } folder ? folder : DefaultCompleteFolder;

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

    /// <summary>
    /// Saves the given settings; null keeps the current value, an empty folder means the default. All fields are
    /// checked first and nothing is saved when one is invalid.
    /// </summary>
    /// <exception cref="ValidationFailedException">One or more fields are invalid (keys: client, nzbFolder, incompleteFolder, completeFolder, speedLimitKBps).</exception>
    public void Update(string? client, string? nzbFolder, string? incompleteFolder, string? completeFolder, long? speedLimitKBps)
    {
        var errors = new Dictionary<string, string[]>();
        DownloadClientKind? kind = null;
        if (client is not null)
        {
            kind = client.Trim().ToLowerInvariant() switch
            {
                BuiltInValue => DownloadClientKind.BuiltIn,
                BlackholeValue => DownloadClientKind.Blackhole,
                _ => null,
            };
            if (kind is null)
            {
                errors["client"] = [$"Use {BuiltInValue} or {BlackholeValue}."];
            }
        }

        var nzb = nzbFolder is null ? null : ValidateFolder(nzbFolder, DefaultNzbFolder, "nzbFolder", "/downloads/nzb or D:\\Downloads\\nzb", errors);
        var incomplete = incompleteFolder is null
            ? null
            : ValidateFolder(incompleteFolder, DefaultIncompleteFolder, "incompleteFolder", "/downloads/incomplete or D:\\Downloads\\incomplete", errors);
        var complete = completeFolder is null
            ? null
            : ValidateFolder(completeFolder, DefaultCompleteFolder, "completeFolder", "/downloads/complete or D:\\Downloads\\complete", errors);

        if (!errors.ContainsKey("incompleteFolder") && !errors.ContainsKey("completeFolder")
            && PathComparer.Instance.Equals(incomplete ?? IncompleteFolder, complete ?? CompleteFolder))
        {
            errors["completeFolder"] = ["Use a different folder than for incomplete downloads."];
        }

        if (speedLimitKBps is < 0 or > MaxSpeedLimitKBps)
        {
            errors["speedLimitKBps"] = [$"Use 0 (unlimited) to {MaxSpeedLimitKBps.ToString("N0", CultureInfo.InvariantCulture)} KB/s."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        if (kind is { } k)
        {
            config.Set(ConfigKeys.DownloadClient, ClientValue(k));
        }

        if (nzb is not null)
        {
            config.Set(ConfigKeys.NzbFolder, PathComparer.Instance.Equals(nzb, DefaultNzbFolder) ? string.Empty : nzb);
        }

        if (incomplete is not null)
        {
            config.Set(ConfigKeys.IncompleteFolder, PathComparer.Instance.Equals(incomplete, DefaultIncompleteFolder) ? string.Empty : incomplete);
        }

        if (complete is not null)
        {
            config.Set(ConfigKeys.CompleteFolder, PathComparer.Instance.Equals(complete, DefaultCompleteFolder) ? string.Empty : complete);
        }

        if (speedLimitKBps is { } limit)
        {
            config.Set(ConfigKeys.DownloadSpeedLimit, limit.ToString(CultureInfo.InvariantCulture));
            _speedLimit = limit;
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

    private static string ValidateFolder(string? path, string defaultFolder, string field, string example, Dictionary<string, string[]> errors)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && !Path.IsPathFullyQualified(trimmed))
        {
            errors[field] = [$"Enter a full path, e.g. {example}."];
            return trimmed;
        }

        var full = trimmed.Length == 0 ? defaultFolder : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (CheckWritable(full) is { } problem)
        {
            errors[field] = [problem];
        }

        return full;
    }
}
