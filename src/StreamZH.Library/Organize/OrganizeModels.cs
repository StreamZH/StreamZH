// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using StreamZH.Core.Media;
using StreamZH.Library.Import;

namespace StreamZH.Library.Organize;

/// <summary>What a renamed file is, for the preview.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<OrganizeFileKind>))]
public enum OrganizeFileKind
{
    Video,
    Subtitle,

    /// <summary>NFO and artwork next to a video (written by the metadata writer or from the release).</summary>
    Metadata,
    Extra,
}

/// <summary>One file that gets a new name; both paths are relative to the item folder (old and new folder).</summary>
public sealed record OrganizeFileChange(string ExistingPath, string NewPath, OrganizeFileKind Kind);

/// <summary>
/// What "Organize" would do with one movie or series: the folder from the folder format, the files from the file formats,
/// and why it cannot be done (conflicts). Items with conflicts are skipped when the run is applied.
/// </summary>
/// <param name="ExistingFolder">Full path of the item folder now.</param>
/// <param name="NewFolder">Full path after organizing (same parent folder).</param>
/// <param name="CaseOnly">The folder name only changes in letter case (renamed in two steps, for Windows).</param>
/// <param name="FolderExists">False: the folder is not on disk yet, only the library path changes.</param>
/// <param name="OtherFileCount">Files in the folder that keep their names and move with the folder (movie.nfo, poster.jpg, …).</param>
/// <param name="OtherFiles">The first few of them (relative paths).</param>
public sealed record OrganizeItemPreview(
    MediaType MediaType,
    int Id,
    string Title,
    int Year,
    string ExistingFolder,
    string NewFolder,
    bool FolderChanged,
    bool CaseOnly,
    bool FolderExists,
    IReadOnlyList<OrganizeFileChange> Files,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Notes,
    int OtherFileCount,
    IReadOnlyList<string> OtherFiles)
{
    public bool HasChanges => FolderChanged || Files.Count > 0;

    public bool CanApply => HasChanges && Conflicts.Count == 0;
}

/// <summary>One movie or series that could not be organized or moved, with the reason.</summary>
public sealed record OrganizeError(int Id, string Title, string Message);

/// <summary>Bulk work on the library that runs as a job: organize, undo or move to another root folder.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<LibraryRunKind>))]
public enum LibraryRunKind
{
    Organize,
    Undo,
    Move,
}

/// <summary>The result of a finished run, shown in the dialog and the status window.</summary>
/// <param name="RunId">The rename log of an organize run (for undo); null for moves and undos.</param>
/// <param name="Done">Items renamed or moved.</param>
/// <param name="Unchanged">Items that already followed the naming scheme.</param>
/// <param name="Skipped">Items left out because of a conflict.</param>
public sealed record LibraryRunResult(
    LibraryRunKind Kind,
    MediaType MediaType,
    int? RunId,
    DateTime Finished,
    int Done,
    int Unchanged,
    int Skipped,
    int Failed,
    IReadOnlyList<OrganizeError> Errors);

/// <summary>The running run: how far it is.</summary>
public sealed record LibraryRunProgress(LibraryRunKind Kind, MediaType MediaType, string Title, int Done, int Total, string? Current, double Progress, bool Started);
