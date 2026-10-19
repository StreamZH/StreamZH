// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Library.Migration;

/// <summary>The apps StreamZH can take over settings and libraries from (0.9).</summary>
public static class MigrationSources
{
    public const string Radarr = "radarr";
    public const string Sonarr = "sonarr";
    public const string Prowlarr = "prowlarr";
    public const string Sabnzbd = "sabnzbd";

    public static readonly IReadOnlyList<string> All = [Radarr, Sonarr, Prowlarr, Sabnzbd];

    public static bool IsKnown(string? source) => source is not null && All.Contains(source);
}

/// <summary>What happens to one item when the migration runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationAction>))]
public enum MigrationAction
{
    /// <summary>A new item is created in StreamZH.</summary>
    [JsonStringEnumMemberName("create")]
    Create,

    /// <summary>An item with the same name/id exists and is updated.</summary>
    [JsonStringEnumMemberName("update")]
    Update,

    /// <summary>Exists already and is only linked (e.g. a movie already in the library gets the file from Radarr).</summary>
    [JsonStringEnumMemberName("link")]
    Link,

    /// <summary>Not taken over; <see cref="MigrationItem.Reason"/> says why.</summary>
    [JsonStringEnumMemberName("skip")]
    Skip,
}

[JsonConverter(typeof(JsonStringEnumConverter<MigrationMessageLevel>))]
public enum MigrationMessageLevel
{
    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warn")]
    Warn,

    [JsonStringEnumMemberName("error")]
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<MigrationRunStatus>))]
public enum MigrationRunStatus
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}

/// <summary>How to reach the old app. The API key is used for this request/run only and never stored.</summary>
public sealed record MigrationConnection(string Url, string ApiKey);

/// <summary>A path as the old app sees it (Remote) and as StreamZH sees it (Local), e.g. /tv → /data/media/tv.</summary>
public sealed record PathMapping(string Remote, string Local);

/// <summary>
/// One item in a preview, e.g. a quality profile or a movie. <see cref="Key"/> is stable between preview and run
/// (e.g. "profile:4", "movie:tmdb:693134") so the user can leave items out.
/// </summary>
public sealed record MigrationItem(
    string Key,
    string Label,
    MigrationAction Action,
    string? Detail = null,
    string? Reason = null,
    string? Warning = null,
    bool NeedsSecret = false);

/// <summary>
/// One section of a source, e.g. "customFormats" of Radarr. <see cref="DefaultSelected"/> sections are ticked in the
/// wizard; <see cref="Advanced"/> ones only show with the Advanced switch (e.g. history).
/// </summary>
public sealed record MigrationSectionPreview(
    string Key,
    string Label,
    string Description,
    int Order,
    bool DefaultSelected,
    bool Advanced,
    IReadOnlyList<MigrationItem> Items,
    IReadOnlyList<string> Warnings)
{
    public MigrationCounts Counts => MigrationCounts.Of(Items);
}

public sealed record MigrationCounts(int Create, int Update, int Link, int Skip)
{
    public static MigrationCounts Of(IEnumerable<MigrationItem> items)
    {
        int create = 0, update = 0, link = 0, skip = 0;
        foreach (var item in items)
        {
            switch (item.Action)
            {
                case MigrationAction.Create: create++; break;
                case MigrationAction.Update: update++; break;
                case MigrationAction.Link: link++; break;
                default: skip++; break;
            }
        }

        return new MigrationCounts(create, update, link, skip);
    }
}

/// <summary>A path the old app uses, mapped to what StreamZH sees, and whether StreamZH can reach it.</summary>
public sealed record MigrationPathCheck(string Remote, string Local, bool Exists, bool Mapped);

/// <summary>What the old app is (name and version as it reports itself).</summary>
public sealed record MigrationAppInfo(string AppName, string Version, string? InstanceName = null);

public sealed record MigrationTestResult(bool Ok, MigrationAppInfo? App, string? Error);

public sealed record MigrationPreview(
    string Source,
    MigrationAppInfo App,
    IReadOnlyList<MigrationSectionPreview> Sections,
    IReadOnlyList<MigrationPathCheck> Paths,
    IReadOnlyList<string> Warnings);

public sealed record MigrationMessage(MigrationMessageLevel Level, string Text, string? Item = null);

/// <summary>The result of one section in a run.</summary>
public sealed record MigrationSectionReport(
    string Key,
    string Label,
    int Created,
    int Updated,
    int Linked,
    int Skipped,
    int Failed,
    IReadOnlyList<MigrationMessage> Messages);

public sealed record MigrationReport(IReadOnlyList<MigrationSectionReport> Sections)
{
    public static readonly MigrationReport Empty = new([]);
}

/// <summary>
/// What the user picked in the wizard. <see cref="Sections"/> null means the default selection;
/// <see cref="Exclude"/> lists item keys to leave out; <see cref="Secrets"/> holds per item key a password/API key the
/// old app does not hand out (e.g. a Usenet server password), typed in by the user — used for this run only.
/// <see cref="Options"/> holds section options, e.g. "movies.monitor" = "asIs".
/// </summary>
public sealed record MigrationRequest(
    string? Url,
    string? ApiKey,
    IReadOnlyList<PathMapping>? PathMappings = null,
    IReadOnlyList<string>? Sections = null,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyDictionary<string, string>? Secrets = null,
    IReadOnlyDictionary<string, string>? Options = null,
    bool DryRun = false);

/// <summary>A run as stored and shown in the wizard and the run history.</summary>
public sealed record MigrationRun(
    int Id,
    string Source,
    MigrationRunStatus Status,
    bool DryRun,
    DateTime Started,
    DateTime? Finished,
    double Progress,
    string? CurrentStep,
    string? Error,
    MigrationAppInfo? App,
    MigrationReport Report);
