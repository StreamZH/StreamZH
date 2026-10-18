// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Import;

namespace StreamZH.Library.Organize;

/// <summary>
/// Works out what "Organize" does with movies or series: the folder name from the folder format of Settings → Media
/// Management, the file names from the file formats (the same naming as the import and "Rename files", quality from what
/// the import stored or else from the file and folder name), plus the subtitles, NFO and artwork that belong to a video.
/// Read-only: nothing on disk changes here.
/// </summary>
public sealed class OrganizePlanner(MediaManagementSettings settings, MovieRepository movies, SeriesRepository series, RenameService rename)
{
    /// <summary>Subtitles that are renamed with the movie even when their name does not start with the video's name.</summary>
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "srt", "ass", "ssa", "sub", "idx", "vtt", "sup", "smi",
    };

    private const int OtherFilesShown = 6;

    /// <summary>Paths are checked against the Windows limit when StreamZH runs on Windows (tests switch it).</summary>
    public bool WindowsPathRules { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Plans for the items in the given order; unknown ids are left out.</summary>
    public IReadOnlyList<OrganizeItemPreview> Plan(MediaType mediaType, IReadOnlyCollection<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var options = settings.Current;
        var builder = new FileNameBuilder(options);
        var result = new List<OrganizeItemPreview>();
        var claimedFolders = new HashSet<string>(PathComparer.Instance);

        if (mediaType == MediaType.Movie)
        {
            var all = movies.All();
            var byId = all.ToDictionary(m => m.Id);
            var others = all.Select(m => (m.Id, m.FolderPath)).Concat(series.All().Select(s => (Id: -s.Id, s.FolderPath))).ToList();
            foreach (var id in ids.Distinct())
            {
                if (byId.TryGetValue(id, out var movie))
                {
                    result.Add(PlanMovie(movie, builder, options, others, claimedFolders));
                }
            }
        }
        else if (mediaType == MediaType.Series)
        {
            var all = series.All();
            var byId = all.ToDictionary(s => s.Id);
            var others = all.Select(s => (s.Id, s.FolderPath)).Concat(movies.All().Select(m => (Id: -m.Id, m.FolderPath))).ToList();
            foreach (var id in ids.Distinct())
            {
                if (byId.TryGetValue(id, out var show))
                {
                    result.Add(PlanSeries(show, builder, options, others, claimedFolders));
                }
            }
        }
        else
        {
            throw new LibraryException("Organize works for movies and series.", LibraryError.Invalid);
        }

        return result;
    }

    public OrganizeItemPreview? PlanOne(MediaType mediaType, int id) => Plan(mediaType, [id]).FirstOrDefault();

    private OrganizeItemPreview PlanMovie(
        Movie movie, FileNameBuilder builder, MediaManagementValues options, List<(int Id, string FolderPath)> others, HashSet<string> claimed)
    {
        var name = builder.MovieFolderName(movie);
        var draft = new Draft(movie.FolderPath, name.Length > 0 ? name : FolderNames.For(movie.Title, movie.Year));

        if (draft.FolderExists && movie.File is { } file)
        {
            if (!DiskScanner.VideoExtensions.Contains(Path.GetExtension(file.RelativePath)))
            {
                draft.Notes.Add("Disc folder (BDMV or VIDEO_TS): the files inside keep their names.");
            }
            else
            {
                if (!options.RenameMovies)
                {
                    draft.Notes.Add("Renaming movie files is off in Settings → Media Management: only the folder is renamed.");
                }

                var video = rename.PreviewMovie(movie.Id).FirstOrDefault();
                var newVideo = video?.NewPath ?? file.RelativePath;
                if (video is not null)
                {
                    draft.Add(video.ExistingPath, video.NewPath, OrganizeFileKind.Video);
                }

                AddSideFiles(draft, file.RelativePath, newVideo);
                if (options.RenameMovies)
                {
                    AddLooseSubtitles(draft, file.RelativePath, newVideo);
                }
            }
        }

        return Finish(MediaType.Movie, movie.Id, movie.Title, movie.Year, draft, others.Where(o => o.Id != movie.Id), claimed);
    }

    private OrganizeItemPreview PlanSeries(
        Series show, FileNameBuilder builder, MediaManagementValues options, List<(int Id, string FolderPath)> others, HashSet<string> claimed)
    {
        var name = builder.SeriesFolderName(show);
        var draft = new Draft(show.FolderPath, name.Length > 0 ? name : FolderNames.For(show.Title, show.Year));
        if (draft.FolderExists)
        {
            if (!options.RenameEpisodes)
            {
                draft.Notes.Add("Renaming episode files is off in Settings → Media Management: only the folder is renamed.");
            }

            foreach (var video in rename.PreviewSeries(show.Id))
            {
                draft.Add(video.ExistingPath, video.NewPath, OrganizeFileKind.Video);
                AddSideFiles(draft, video.ExistingPath, video.NewPath);
            }
        }

        return Finish(MediaType.Series, show.Id, show.Title, show.Year, draft, others.Where(o => o.Id != show.Id), claimed);
    }

    /// <summary>"&lt;video&gt;.de.srt", "&lt;video&gt;.nfo", "&lt;video&gt;-thumb.jpg" next to the video get the new name too.</summary>
    private static void AddSideFiles(Draft draft, string videoPath, string newVideoPath)
    {
        var oldBase = Path.GetFileNameWithoutExtension(videoPath);
        var newBase = Path.GetFileNameWithoutExtension(newVideoPath);
        var directory = Path.GetDirectoryName(Path.Combine(draft.OldFolder, videoPath))!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var newDirectory = Path.GetDirectoryName(newVideoPath) ?? string.Empty;
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (DiskScanner.VideoExtensions.Contains(Path.GetExtension(path))
                || !(fileName.StartsWith(oldBase + ".", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith(oldBase + "-thumb.", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relative = Path.GetRelativePath(draft.OldFolder, path);
            var target = Path.Combine(newDirectory, newBase + fileName[oldBase.Length..]);
            draft.Add(relative, target, KindOf(path));
        }
    }

    /// <summary>
    /// Subtitles anywhere in the movie folder (e.g. "Subs/2_English.srt") are named after the movie, with their language
    /// and flags, like Radarr's extra file import. A second subtitle that would get the same name keeps its own.
    /// </summary>
    private static void AddLooseSubtitles(Draft draft, string videoPath, string newVideoPath)
    {
        var oldBase = Path.GetFileNameWithoutExtension(videoPath);
        var newBase = Path.GetFileNameWithoutExtension(newVideoPath);
        var newDirectory = Path.GetDirectoryName(newVideoPath) ?? string.Empty;
        foreach (var path in ExtraFiles.Find(draft.OldFolder, SubtitleExtensions))
        {
            var relative = Path.GetRelativePath(draft.OldFolder, path);
            if (draft.IsSource(relative))
            {
                continue;
            }

            var target = Path.Combine(newDirectory, ExtraFiles.TargetName(path, oldBase, newBase));
            if (draft.IsTarget(target) || FolderMover.TargetTaken(path, Path.Combine(draft.OldFolder, target)))
            {
                draft.Notes.Add($"{relative} keeps its name: another subtitle gets the name {Path.GetFileName(target)}.");
                continue;
            }

            draft.Add(relative, target, OrganizeFileKind.Subtitle);
        }
    }

    private OrganizeItemPreview Finish(
        MediaType mediaType, int id, string title, int year, Draft draft, IEnumerable<(int Id, string FolderPath)> others, HashSet<string> claimed)
    {
        var conflicts = new List<string>();
        var folderChanged = !string.Equals(draft.OldFolder, draft.NewFolder, StringComparison.Ordinal);
        var caseOnly = folderChanged && FolderMover.IsCaseOnly(draft.OldFolder, draft.NewFolder);
        if (folderChanged)
        {
            if (caseOnly)
            {
                draft.Notes.Add("Only the letter case of the folder changes; it is renamed in two steps so this works on Windows too.");
            }

            if (!draft.FolderExists)
            {
                draft.Notes.Add("The folder is not on disk yet: only the library path changes.");
            }

            if (FolderMover.TargetTaken(draft.OldFolder, draft.NewFolder))
            {
                conflicts.Add($"A folder named \"{Path.GetFileName(draft.NewFolder)}\" already exists in {Path.GetDirectoryName(draft.NewFolder)}.");
            }
            else if (others.Any(o => PathComparer.Instance.Equals(o.FolderPath, draft.NewFolder)))
            {
                conflicts.Add($"Another item in the library already uses the folder \"{Path.GetFileName(draft.NewFolder)}\".");
            }

            if (PathProblem(draft.NewFolder) is { } tooLong)
            {
                conflicts.Add(tooLong);
            }
        }

        if (folderChanged || draft.Files.Count > 0)
        {
            if (!claimed.Add(draft.NewFolder) && folderChanged)
            {
                conflicts.Add($"Another selected item gets the same folder \"{Path.GetFileName(draft.NewFolder)}\".");
            }
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in draft.Files)
        {
            if (!targets.Add(file.NewPath))
            {
                conflicts.Add($"Two files would be named {file.NewPath}.");
                continue;
            }

            // Before the run the new folder is still the old one: look there.
            var source = Path.Combine(draft.OldFolder, file.ExistingPath);
            var target = Path.Combine(draft.OldFolder, file.NewPath);
            if (FolderMover.TargetTaken(source, target) && !draft.IsSource(file.NewPath))
            {
                conflicts.Add($"{file.NewPath} already exists.");
            }

            if (PathProblem(Path.Combine(draft.NewFolder, file.NewPath)) is { } tooLong)
            {
                conflicts.Add(tooLong);
            }
        }

        var (otherCount, otherFiles) = draft.FolderExists && (folderChanged || draft.Files.Count > 0) ? draft.OtherFiles() : (0, []);
        return new OrganizeItemPreview(
            mediaType, id, title, year, draft.OldFolder, draft.NewFolder, folderChanged, caseOnly, draft.FolderExists, draft.Files,
            [.. conflicts.Distinct(StringComparer.Ordinal)], draft.Notes, otherCount, otherFiles);
    }

    private string? PathProblem(string path) => FolderMover.PathProblem(path, WindowsPathRules);

    private static OrganizeFileKind KindOf(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        if (SubtitleExtensions.Contains(extension))
        {
            return OrganizeFileKind.Subtitle;
        }

        return extension.ToLowerInvariant() is "nfo" or "nfo-orig" or "jpg" or "jpeg" or "png" or "tbn" ? OrganizeFileKind.Metadata : OrganizeFileKind.Extra;
    }

    /// <summary>The plan of one item while it is put together.</summary>
    private sealed class Draft(string oldFolder, string newName)
    {
        public string OldFolder { get; } = Path.TrimEndingDirectorySeparator(oldFolder);

        public string NewFolder { get; } = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(oldFolder)) ?? string.Empty, newName);

        public bool FolderExists { get; } = Directory.Exists(oldFolder);

        public List<OrganizeFileChange> Files { get; } = [];

        public List<string> Notes { get; } = [];

        public void Add(string existing, string target, OrganizeFileKind kind)
        {
            if (!string.Equals(existing.Replace('\\', '/'), target.Replace('\\', '/'), StringComparison.Ordinal) && !IsSource(existing))
            {
                Files.Add(new OrganizeFileChange(existing, target, kind));
            }
        }

        public bool IsSource(string relative) => Files.Any(f => string.Equals(f.ExistingPath, relative, StringComparison.Ordinal));

        public bool IsTarget(string relative) => Files.Any(f => string.Equals(f.NewPath, relative, StringComparison.OrdinalIgnoreCase));

        public (int Count, IReadOnlyList<string> First) OtherFiles()
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var rest = Directory.EnumerateFiles(OldFolder, "*", options)
                .Select(f => Path.GetRelativePath(OldFolder, f))
                .Where(f => !IsSource(f))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (rest.Count, [.. rest.Take(OtherFilesShown)]);
        }
    }
}
