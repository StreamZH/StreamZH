// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Library;

/// <summary>Where a root folder or quality profile of the old app ends up in StreamZH, and what the user should know about it.</summary>
public sealed record TargetChoice(int? Id, string Name, string? Warning, bool WillCreate = false);

/// <summary>
/// Picks StreamZH's root folder and quality profile for a movie or series of the old app: the mapping a settings section
/// of the same run recorded, else the same path or name, else (root folders) a StreamZH root folder that contains the
/// item's folder or, during the run, a new root folder for the old app's root, else StreamZH's default with a warning.
/// </summary>
public sealed class LibraryTargets(
    MigrationContext context,
    MediaType mediaType,
    IReadOnlyList<ArrRootFolder> oldRoots,
    IReadOnlyList<ArrQualityProfile> oldProfiles,
    RootFolderRepository rootFolders,
    QualityProfileService profiles,
    RootFolderService? rootFolderService = null)
{
    private readonly Dictionary<string, TargetChoice> _roots = new(PathComparer.Instance);
    private readonly Dictionary<int, TargetChoice> _profiles = [];
    private List<RootFolder> _streamZhRoots = [.. rootFolders.All().Where(r => r.MediaType == mediaType)];

    private string App => context.App.AppName;

    private string Kind => mediaType == MediaType.Movie ? "movie" : "series";

    /// <summary>
    /// The root folder for an item whose old root is <paramref name="oldRootPath"/> and whose folder (mapped) is
    /// <paramref name="localFolder"/>. With <paramref name="allowCreate"/> a missing root folder is added to StreamZH.
    /// </summary>
    public TargetChoice RootFolder(string? oldRootPath, string localFolder, bool allowCreate)
    {
        var remoteRoot = oldRootPath is { Length: > 0 } ? oldRootPath.TrimEnd('/', '\\') : null;
        var key = remoteRoot ?? string.Empty;
        if (key.Length > 0 && _roots.TryGetValue(key, out var cached) && (cached.Id is not null || !allowCreate))
        {
            return cached;
        }

        var choice = ChooseRoot(remoteRoot, localFolder, allowCreate);
        if (key.Length > 0 && choice.Warning is null)
        {
            _roots[key] = choice;
        }

        return choice;
    }

    private TargetChoice ChooseRoot(string? remoteRoot, string localFolder, bool allowCreate)
    {
        if (remoteRoot is not null && oldRoots.FirstOrDefault(r => r.Path is { } p && PathComparer.Instance.Equals(p, remoteRoot)) is { } old
            && context.Ids.Get("rootFolder", old.Id) is { } mappedId && _streamZhRoots.FirstOrDefault(r => r.Id == mappedId) is { } mapped)
        {
            return new TargetChoice(mapped.Id, mapped.Path, null);
        }

        var localRoot = remoteRoot is null ? null : context.Paths.Map(remoteRoot);
        if (localRoot is not null && _streamZhRoots.FirstOrDefault(r => PathComparer.Instance.Equals(r.Path, localRoot)) is { } same)
        {
            return new TargetChoice(same.Id, same.Path, null);
        }

        if (_streamZhRoots.Where(r => IsInside(localFolder, r.Path)).MaxBy(r => r.Path.Length) is { } containing)
        {
            return new TargetChoice(containing.Id, containing.Path, null);
        }

        if (localRoot is not null && context.Paths.FolderExists(localRoot) && !_streamZhRoots.Any(r => IsInside(r.Path, localRoot) || IsInside(localRoot, r.Path)))
        {
            if (!allowCreate)
            {
                return new TargetChoice(null, localRoot, null, WillCreate: true);
            }

            if (rootFolderService is not null)
            {
                try
                {
                    var added = rootFolderService.Add(localRoot, mediaType);
                    _streamZhRoots = [.. rootFolders.All().Where(r => r.MediaType == mediaType)];
                    return new TargetChoice(added.Id, added.Path, null);
                }
                catch (LibraryException)
                {
                    // Falls through to the default root folder below.
                }
            }
        }

        if (_streamZhRoots.FirstOrDefault() is { } fallback)
        {
            return new TargetChoice(
                fallback.Id,
                fallback.Path,
                $"{App}'s root folder {remoteRoot ?? "(unknown)"} has no match in StreamZH; the {Kind} is filed under {fallback.Path} (its folder stays where it is).");
        }

        return new TargetChoice(
            null,
            string.Empty,
            $"StreamZH has no {Kind} root folder. Take over {App}'s root folders or add one in Settings → Media Management.");
    }

    /// <summary>The quality profile for the old app's profile id.</summary>
    public TargetChoice Profile(int oldProfileId)
    {
        if (_profiles.TryGetValue(oldProfileId, out var cached))
        {
            return cached;
        }

        var all = profiles.All();
        TargetChoice choice;
        var oldName = oldProfiles.FirstOrDefault(p => p.Id == oldProfileId)?.Name?.Trim();
        if (context.Ids.Get("qualityProfile", oldProfileId) is { } mappedId && all.FirstOrDefault(p => p.Id == mappedId) is { } mapped)
        {
            choice = new TargetChoice(mapped.Id, mapped.Name, null);
        }
        else if (oldName is { Length: > 0 } && all.FirstOrDefault(p => p.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            choice = new TargetChoice(byName.Id, byName.Name, null);
        }
        else
        {
            var defaultId = profiles.DefaultProfileId();
            var name = all.FirstOrDefault(p => p.Id == defaultId)?.Name ?? "the default profile";
            choice = new TargetChoice(
                defaultId,
                name,
                $"{App}'s quality profile '{oldName ?? oldProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)}' is not in StreamZH; '{name}' is used. Take over the quality profiles to keep it.");
        }

        _profiles[oldProfileId] = choice;
        return choice;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
        {
            return false;
        }

        var p = path.TrimEnd('/', '\\');
        var f = folder.TrimEnd('/', '\\');
        if (PathComparer.Instance.Equals(p, f))
        {
            return true;
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return p.Length > f.Length && p.StartsWith(f, comparison) && p[f.Length] is '/' or '\\';
    }
}
