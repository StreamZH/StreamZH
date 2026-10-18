// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamZH.Library.Import;

namespace StreamZH.Library.Renamer;

// The file renamer (Tools → File renamer): a source folder, a filter, an ordered list of rules, a preview and a logged,
// undoable run. Everything here is plain data shared by the API, the presets and the history.

/// <summary>What the renamer works on in the source folder.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RenameTarget>))]
public enum RenameTarget
{
    Files,
    Folders,
    FilesAndFolders,
}

/// <summary>A folder on the StreamZH machine, or the folder of a library movie or series.</summary>
public sealed record RenamerSource
{
    public const int MaxDepth = 20;

    public string? Path { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }

    /// <summary>Also look in subfolders, down to <see cref="Depth"/> levels.</summary>
    public bool Subfolders { get; init; }

    public int Depth { get; init; } = 5;
    public RenameTarget Target { get; init; } = RenameTarget.Files;
}

[JsonConverter(typeof(CamelCaseEnumConverter<ExtensionPreset>))]
public enum ExtensionPreset
{
    Video,
    VideoAndSubtitles,
    Subtitles,
    Images,
    All,
    Custom,
}

/// <summary>Which files of the source are renamed. Folders are only filtered by name.</summary>
public sealed record RenamerFilter
{
    public ExtensionPreset Extensions { get; init; } = ExtensionPreset.Video;

    /// <summary>For <see cref="ExtensionPreset.Custom"/>: "mkv, mp4, srt".</summary>
    public string CustomExtensions { get; init; } = string.Empty;

    /// <summary>Glob ("*S01*", several separated by ;) or /regex/ (/regex/i ignores case) the name must match.</summary>
    public string Include { get; init; } = string.Empty;

    /// <summary>Glob or /regex/ of names to leave out.</summary>
    public string Exclude { get; init; } = string.Empty;

    public double? MinSizeMb { get; init; }
    public double? MaxSizeMb { get; init; }

    /// <summary>Leaves out sample files and folders named "sample".</summary>
    public bool HideSamples { get; init; } = true;
}

/// <summary>Part of the name a rule works on: the name without extension (default) or the whole name with extension.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RuleScope>))]
public enum RuleScope
{
    Name,
    FullName,
}

/// <summary>One step of the renaming; rules run in order, each on the result of the one before.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ReplaceRule), "replace")]
[JsonDerivedType(typeof(RemoveRule), "remove")]
[JsonDerivedType(typeof(InsertRule), "insert")]
[JsonDerivedType(typeof(CaseRule), "case")]
[JsonDerivedType(typeof(CounterRule), "counter")]
[JsonDerivedType(typeof(ExtensionRule), "extension")]
[JsonDerivedType(typeof(TrimRule), "trim")]
[JsonDerivedType(typeof(MediaRule), "media")]
public abstract record RenameRule
{
    public bool Enabled { get; init; } = true;
}

[JsonConverter(typeof(CamelCaseEnumConverter<ReplaceOccurrence>))]
public enum ReplaceOccurrence
{
    All,
    First,
    Last,
}

/// <summary>Replace text, plain or a regular expression with $1, ${name} in the replacement.</summary>
public sealed record ReplaceRule : RenameRule
{
    public string Find { get; init; } = string.Empty;
    public string ReplaceWith { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public bool CaseSensitive { get; init; }
    public ReplaceOccurrence Occurrence { get; init; } = ReplaceOccurrence.All;
    public RuleScope Scope { get; init; } = RuleScope.Name;
}

[JsonConverter(typeof(CamelCaseEnumConverter<RemoveMode>))]
public enum RemoveMode
{
    /// <summary>Count characters from a position (from the start or the end).</summary>
    Characters,

    /// <summary>Text between two markers, e.g. "(" and ")".</summary>
    Between,

    /// <summary>[...], (...) and {...} with their content.</summary>
    Brackets,

    /// <summary>Dots and underscores become spaces.</summary>
    Separators,

    /// <summary>Several spaces become one.</summary>
    Spaces,
}

public sealed record RemoveRule : RenameRule
{
    public RemoveMode Mode { get; init; } = RemoveMode.Brackets;

    /// <summary>Characters: first character to remove, 1 = the first (or the last with <see cref="FromEnd"/>).</summary>
    public int Position { get; init; } = 1;

    public int Count { get; init; } = 1;
    public bool FromEnd { get; init; }

    public string StartMarker { get; init; } = "(";
    public string EndMarker { get; init; } = ")";

    /// <summary>Between: the markers go too.</summary>
    public bool IncludeMarkers { get; init; } = true;

    public bool Square { get; init; } = true;
    public bool Round { get; init; } = true;
    public bool Curly { get; init; } = true;

    /// <summary>Separators: a dot between digits stays ("5.1", "1.5").</summary>
    public bool KeepDecimalDots { get; init; } = true;

    public RuleScope Scope { get; init; } = RuleScope.Name;
}

[JsonConverter(typeof(CamelCaseEnumConverter<InsertPosition>))]
public enum InsertPosition
{
    Prefix,
    Suffix,
    Position,
}

/// <summary>Adds text with tokens: {name}, {ext}, {folder}, {date}, {time}, {year}, {index}.</summary>
public sealed record InsertRule : RenameRule
{
    public string Text { get; init; } = string.Empty;
    public InsertPosition Where { get; init; } = InsertPosition.Prefix;

    /// <summary>Position: insert before this character (1 = at the start; from the end with <see cref="FromEnd"/>).</summary>
    public int Position { get; init; } = 1;

    public bool FromEnd { get; init; }
    public RuleScope Scope { get; init; } = RuleScope.Name;
}

[JsonConverter(typeof(CamelCaseEnumConverter<CaseMode>))]
public enum CaseMode
{
    Title,
    Lower,
    Upper,
    Sentence,
}

public sealed record CaseRule : RenameRule
{
    public CaseMode Mode { get; init; } = CaseMode.Title;

    /// <summary>Small words that stay lower case in Title Case: en, de, fr, es, it, nl, pt, or "none".</summary>
    public string Language { get; init; } = "en";

    /// <summary>Words written in capitals (FBI, NCIS) are kept in Title and Sentence case.</summary>
    public bool KeepCapitals { get; init; } = true;

    public RuleScope Scope { get; init; } = RuleScope.Name;
}

[JsonConverter(typeof(CamelCaseEnumConverter<CounterPosition>))]
public enum CounterPosition
{
    Prefix,
    Suffix,
    Position,

    /// <summary>The counter is the whole name.</summary>
    Replace,
}

public sealed record CounterRule : RenameRule
{
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;

    /// <summary>Minimum digits: 2 gives 01, 02 …</summary>
    public int Padding { get; init; } = 2;

    public CounterPosition Where { get; init; } = CounterPosition.Prefix;
    public int Position { get; init; } = 1;

    /// <summary>Text between the counter and the name.</summary>
    public string Separator { get; init; } = " - ";

    /// <summary>Starts again in every folder.</summary>
    public bool PerFolder { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<ExtensionMode>))]
public enum ExtensionMode
{
    Lower,
    Upper,
    Change,
    Remove,
}

public sealed record ExtensionRule : RenameRule
{
    public ExtensionMode Mode { get; init; } = ExtensionMode.Lower;
    public string NewExtension { get; init; } = string.Empty;
}

[JsonConverter(typeof(CamelCaseEnumConverter<TrimWhere>))]
public enum TrimWhere
{
    Both,
    Start,
    End,
}

public sealed record TrimRule : RenameRule
{
    public const string DefaultCharacters = " .-_";

    public string Characters { get; init; } = DefaultCharacters;
    public TrimWhere Where { get; init; } = TrimWhere.Both;

    /// <summary>Cuts longer names to this many characters.</summary>
    public int? MaxLength { get; init; }

    public RuleScope Scope { get; init; } = RuleScope.Name;
}

[JsonConverter(typeof(CamelCaseEnumConverter<MediaKind>))]
public enum MediaKind
{
    Auto,
    Episode,
    Movie,
}

[JsonConverter(typeof(CamelCaseEnumConverter<MediaMatch>))]
public enum MediaMatch
{
    /// <summary>The library first, then TMDB.</summary>
    LibraryAndTmdb,
    Library,
    Tmdb,

    /// <summary>Only what the file name says (no episode titles).</summary>
    None,
}

/// <summary>
/// Reads the name with StreamZH's release parser, finds the movie or episodes in the library or on TMDB and builds the
/// name from a naming format (empty = the format from Settings → Media Management).
/// </summary>
public sealed record MediaRule : RenameRule
{
    public MediaKind Kind { get; init; } = MediaKind.Auto;
    public MediaMatch Match { get; init; } = MediaMatch.LibraryAndTmdb;
    public string EpisodeFormat { get; init; } = string.Empty;
    public string MovieFormat { get; init; } = string.Empty;

    /// <summary>Every file is in this season (for names with an episode number only, or wrong season numbers).</summary>
    public int? Season { get; init; }

    /// <summary>Added to every episode number (e.g. -12 when a release numbers season 2 from 13).</summary>
    public int EpisodeOffset { get; init; }

    /// <summary>Look up this title instead of the one in the name.</summary>
    public string SearchTitle { get; init; } = string.Empty;

    /// <summary>This TMDB id instead of a search.</summary>
    public int? TmdbId { get; init; }

    /// <summary>Names without a title ("01.mkv", "S01E02.mkv") take it from the folder (or the folder above "Season 1").</summary>
    public bool UseFolderName { get; init; } = true;
}

/// <summary>Options of the run.</summary>
public sealed record RenamerOptions
{
    /// <summary>Moves each file into this subfolder of its folder, e.g. "Season {season:00}" (empty = stays).</summary>
    public string FolderFormat { get; init; } = string.Empty;

    /// <summary>Subtitles and other files named like a renamed video ("Show.S01E01.de.srt") follow it.</summary>
    public bool Companions { get; init; } = true;
}

public sealed record RenamerRequest
{
    public RenamerSource Source { get; init; } = new();
    public RenamerFilter Filter { get; init; } = new();
    public IReadOnlyList<RenameRule> Rules { get; init; } = [];
    public RenamerOptions Options { get; init; } = new();

    /// <summary>Keys (relative paths) of rows the user switched off in the preview.</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];
}

// ------------------------------------------------------------------ results

/// <summary>A file or folder of the source.</summary>
/// <param name="Key">Path relative to the source folder, with forward slashes.</param>
/// <param name="Folder">Relative folder ("" = the source folder itself).</param>
public sealed record ScanEntry(string Key, string Folder, string Name, bool IsFolder, long Size, DateTime Modified);

public sealed record ScanResult(
    string Folder,
    int Total,
    int Matched,
    IReadOnlyList<ScanEntry> Entries,
    bool Truncated,
    int? MovieId,
    int? SeriesId,
    string? ItemTitle);

[JsonConverter(typeof(CamelCaseEnumConverter<RowStatus>))]
public enum RowStatus
{
    Unchanged,
    Rename,
    Conflict,
    Error,
    Excluded,
}

/// <summary>Why a row cannot be renamed (codes for the UI; the message says it in words).</summary>
public static class RowProblems
{
    public const string Duplicate = "duplicate";
    public const string Exists = "exists";
    public const string Invalid = "invalid";
    public const string TooLong = "tooLong";
    public const string Empty = "empty";
    public const string LibraryFolder = "libraryFolder";
    public const string Error = "error";

    /// <summary>A subtitle or other companion whose video is not renamed.</summary>
    public const string Follows = "follows";
}

/// <summary>What the media rule found for a file.</summary>
/// <param name="Kind">"episode" or "movie".</param>
/// <param name="Source">"library", "tmdb" or "name" (nothing found, only what the name says).</param>
public sealed record MediaMatchInfo(
    string Kind,
    string Title,
    int Year,
    int? Season,
    IReadOnlyList<int> Episodes,
    string? EpisodeTitle,
    string Source,
    string? Note);

public sealed record PreviewRow(
    string Key,
    string Folder,
    string OldName,
    string NewName,
    string NewFolder,
    bool IsFolder,
    long Size,
    RowStatus Status,
    string? Problem,
    string? Message,
    string? CompanionOf,
    MediaMatchInfo? Media,
    int? MovieId,
    int? SeriesId);

public sealed record PreviewCounts(int Total, int Unchanged, int Rename, int Conflict, int Error, int Excluded);

/// <param name="RuleErrors">Per rule (index in the list): what is wrong; such rules are skipped in the preview.</param>
public sealed record PreviewResult(
    string Folder,
    int Total,
    int Matched,
    bool Truncated,
    IReadOnlyList<PreviewRow> Rows,
    PreviewCounts Counts,
    IReadOnlyDictionary<int, string> RuleErrors,
    string? FolderFormatError);

[JsonConverter(typeof(CamelCaseEnumConverter<RunState>))]
public enum RunState
{
    Queued,
    Running,
    Completed,
    Partial,
    Failed,
    Undoing,
    Undone,
}

[JsonConverter(typeof(CamelCaseEnumConverter<RunItemState>))]
public enum RunItemState
{
    Planned,
    Done,
    Failed,
    Skipped,
    Undone,
}

[JsonConverter(typeof(CamelCaseEnumConverter<RunItemKind>))]
public enum RunItemKind
{
    Rename,

    /// <summary>A folder the run created for "move into folder"; undo removes it when empty.</summary>
    CreateFolder,

    /// <summary>A source folder the run left empty and removed (OldPath); undo creates it again before moving files back.</summary>
    RemoveFolder,
}

public sealed record RenamerRunItem(int Order, RunItemKind Kind, string OldPath, string NewPath, bool IsFolder, RunItemState State, string? Error);

public sealed record RenamerRun(
    int Id,
    DateTime Started,
    DateTime? Finished,
    string Folder,
    RunState State,
    int Total,
    int Done,
    int Failed,
    string? Error,
    string Summary,
    DateTime? UndoneAt,
    IReadOnlyList<RenameRule> Rules);

public sealed record RenamerRunDetail(RenamerRun Run, IReadOnlyList<RenamerRunItem> Items);

/// <summary>What a preset keeps: the filter, the rules and the options (not the folder).</summary>
public sealed record RenamerPresetContent
{
    public RenamerFilter Filter { get; init; } = new();
    public IReadOnlyList<RenameRule> Rules { get; init; } = [];
    public RenamerOptions Options { get; init; } = new();
}

public sealed record RenamerPreset(int Id, string Name, RenamerPresetContent Content, DateTime Updated);

/// <summary>JSON for rules in the database and the request bodies (the "type" may come after the other fields).</summary>
public static class RenamerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };
}
