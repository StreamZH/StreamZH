// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Data;

namespace StreamZH.Library.ImportLists;

/// <summary>
/// Radarr's "Clean library level": what happens to items an import list added once they are on no list any more.
/// StreamZH only ever touches items a list added (never ones you added yourself) and never deletes files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CleanLibraryLevel>))]
public enum CleanLibraryLevel
{
    [JsonStringEnumMemberName("disabled")]
    Disabled,

    /// <summary>Only writes to the log what would happen.</summary>
    [JsonStringEnumMemberName("logOnly")]
    LogOnly,

    /// <summary>Stops monitoring the item; it stays in the library.</summary>
    [JsonStringEnumMemberName("keepAndUnmonitor")]
    KeepAndUnmonitor,

    /// <summary>Removes the item from the library; its files stay on disk.</summary>
    [JsonStringEnumMemberName("removeAndKeepFiles")]
    RemoveAndKeepFiles,
}

/// <param name="SyncInterval">Hours between two syncs of all lists ("List update interval").</param>
public sealed record ImportListOptionValues(int SyncInterval, CleanLibraryLevel CleanLibraryLevel);

/// <summary>Settings → Import lists → Options, read from the config table on every use.</summary>
public sealed class ImportListOptions(ConfigRepository config)
{
    /// <summary>Like Radarr: every 24 hours by default, at least every 6 hours so the list services are not hammered.</summary>
    public const int DefaultSyncInterval = 24;
    public const int MinSyncInterval = 6;
    public const int MaxSyncInterval = 720;

    public static readonly ImportListOptionValues Defaults = new(DefaultSyncInterval, CleanLibraryLevel.Disabled);

    public ImportListOptionValues Values => new(
        int.TryParse(config.Get(ConfigKeys.ImportListSyncInterval), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, MinSyncInterval, MaxSyncInterval)
            : DefaultSyncInterval,
        Enum.TryParse<CleanLibraryLevel>(config.Get(ConfigKeys.ImportListCleanLibraryLevel), ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : Defaults.CleanLibraryLevel);

    public TimeSpan SyncInterval => TimeSpan.FromHours(Values.SyncInterval);

    /// <summary>Saves values that were validated by the caller.</summary>
    public void Save(ImportListOptionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        config.Set(ConfigKeys.ImportListSyncInterval, values.SyncInterval.ToString(CultureInfo.InvariantCulture));
        config.Set(ConfigKeys.ImportListCleanLibraryLevel, values.CleanLibraryLevel.ToString());
    }

    /// <summary>Field errors (camelCase) of the values, empty when they are fine.</summary>
    public static Dictionary<string, string[]> Validate(ImportListOptionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = new Dictionary<string, string[]>();
        if (values.SyncInterval is < MinSyncInterval or > MaxSyncInterval)
        {
            errors["syncInterval"] = [$"Use {MinSyncInterval} to {MaxSyncInterval} hours."];
        }

        if (!Enum.IsDefined(values.CleanLibraryLevel))
        {
            errors["cleanLibraryLevel"] = ["Use disabled, logOnly, keepAndUnmonitor or removeAndKeepFiles."];
        }

        return errors;
    }
}
