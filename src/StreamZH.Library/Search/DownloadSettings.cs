// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>
/// Where grabbed NZB files go. Until the built-in download engine arrives (0.5), SABnzbd or NZBGet can watch this folder.
/// </summary>
public sealed class DownloadSettings(ConfigRepository config, AppPaths paths)
{
    public string DefaultNzbFolder => Path.Combine(paths.DataDirectory, "downloads", "nzb");

    public string NzbFolder => config.Get(ConfigKeys.NzbFolder) is { Length: > 0 } folder ? folder : DefaultNzbFolder;

    /// <summary>Saves the NZB folder (empty = default). Creates it when missing and checks that StreamZH can write there.</summary>
    /// <exception cref="ValidationFailedException">The path is not a full path or not writable.</exception>
    public string SetNzbFolder(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && !Path.IsPathFullyQualified(trimmed))
        {
            throw Invalid("Enter a full path, e.g. /downloads/nzb or D:\\Downloads\\nzb.");
        }

        var full = trimmed.Length == 0 ? DefaultNzbFolder : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (CheckWritable(full) is { } problem)
        {
            throw Invalid(problem);
        }

        config.Set(ConfigKeys.NzbFolder, PathComparer.Instance.Equals(full, DefaultNzbFolder) ? string.Empty : full);
        return full;
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

    private static ValidationFailedException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["nzbFolder"] = [message] });
}
