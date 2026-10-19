// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;

namespace StreamZH.Library.Requests;

/// <summary>Which episodes of a requested series are monitored once it is approved.</summary>
public static class RequestSeriesMonitor
{
    /// <summary>Only the requested seasons (all seasons when the whole series was requested).</summary>
    public const string Requested = "requested";

    /// <summary>Every season (specials stay unmonitored).</summary>
    public const string All = "all";

    /// <summary>The requested seasons and every episode that has not aired yet, so new seasons come too.</summary>
    public const string Future = "future";

    public static readonly IReadOnlyList<string> Values = [Requested, All, Future];
}

/// <summary>Settings → Requests (0.10). Null ids mean "the default": the default quality profile, the first root folder.</summary>
/// <param name="DefaultMovieLimit">Movie requests per <paramref name="LimitDays"/> for friends and requesters; null = no limit.</param>
/// <param name="SearchOnApprove">Search right after approving (never while side-by-side mode holds automatic grabs).</param>
public sealed record RequestSettingsValues(
    bool Enabled = true,
    int? DefaultMovieProfileId = null,
    int? DefaultSeriesProfileId = null,
    int? DefaultMovieRootFolderId = null,
    int? DefaultSeriesRootFolderId = null,
    bool AutoApproveFriends = true,
    bool AutoApproveRequesters = false,
    int? DefaultMovieLimit = null,
    int? DefaultSeriesLimit = null,
    int LimitDays = RequestSettings.DefaultLimitDays,
    bool SearchOnApprove = true,
    string SeriesMonitor = RequestSeriesMonitor.Requested,
    bool NotifyOnAvailable = true);

/// <summary>
/// Settings → Requests, read from the config table on every use. Also the <see cref="IRequestDefaults"/> of the users
/// without own values: owners and admins always approve themselves, friends and requesters as set here.
/// </summary>
public sealed class RequestSettings(ConfigRepository config, QualityProfileRepository? profiles = null, RootFolderRepository? rootFolders = null) : IRequestDefaults
{
    public const int DefaultLimitDays = 7;
    public const int MaxLimitDays = 365;
    public const int MaxLimit = 1000;

    public RequestSettingsValues Values => new(
        Bool(ConfigKeys.RequestsEnabled, true),
        Int(ConfigKeys.RequestsMovieProfileId),
        Int(ConfigKeys.RequestsSeriesProfileId),
        Int(ConfigKeys.RequestsMovieRootFolderId),
        Int(ConfigKeys.RequestsSeriesRootFolderId),
        Bool(ConfigKeys.RequestsAutoApproveFriends, true),
        Bool(ConfigKeys.RequestsAutoApproveRequesters, false),
        Int(ConfigKeys.RequestsMovieLimit),
        Int(ConfigKeys.RequestsSeriesLimit),
        Int(ConfigKeys.RequestsLimitDays) is { } days ? Math.Clamp(days, 1, MaxLimitDays) : DefaultLimitDays,
        Bool(ConfigKeys.RequestsSearchOnApprove, true),
        config.Get(ConfigKeys.RequestsSeriesMonitor) is { } monitor && RequestSeriesMonitor.Values.Contains(monitor) ? monitor : RequestSeriesMonitor.Requested,
        Bool(ConfigKeys.RequestsNotifyOnAvailable, true));

    public bool AutoApprove(UserRole role) => role switch
    {
        UserRole.Owner or UserRole.Admin => true,
        UserRole.Friend => Values.AutoApproveFriends,
        _ => Values.AutoApproveRequesters,
    };

    public int? MovieLimit => Values.DefaultMovieLimit;

    public int? SeriesLimit => Values.DefaultSeriesLimit;

    public int LimitDays => Values.LimitDays;

    /// <summary>True when the quality profile exists (or profiles cannot be checked here).</summary>
    public bool ProfileExists(int id) => profiles?.Exists(id) ?? true;

    /// <summary>Saves values that passed <see cref="Validate"/>.</summary>
    public void Save(RequestSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        config.Set(ConfigKeys.RequestsEnabled, Text(values.Enabled));
        config.Set(ConfigKeys.RequestsMovieProfileId, Text(values.DefaultMovieProfileId));
        config.Set(ConfigKeys.RequestsSeriesProfileId, Text(values.DefaultSeriesProfileId));
        config.Set(ConfigKeys.RequestsMovieRootFolderId, Text(values.DefaultMovieRootFolderId));
        config.Set(ConfigKeys.RequestsSeriesRootFolderId, Text(values.DefaultSeriesRootFolderId));
        config.Set(ConfigKeys.RequestsAutoApproveFriends, Text(values.AutoApproveFriends));
        config.Set(ConfigKeys.RequestsAutoApproveRequesters, Text(values.AutoApproveRequesters));
        config.Set(ConfigKeys.RequestsMovieLimit, Text(values.DefaultMovieLimit));
        config.Set(ConfigKeys.RequestsSeriesLimit, Text(values.DefaultSeriesLimit));
        config.Set(ConfigKeys.RequestsLimitDays, Text(values.LimitDays));
        config.Set(ConfigKeys.RequestsSearchOnApprove, Text(values.SearchOnApprove));
        config.Set(ConfigKeys.RequestsSeriesMonitor, values.SeriesMonitor);
        config.Set(ConfigKeys.RequestsNotifyOnAvailable, Text(values.NotifyOnAvailable));
    }

    /// <summary>Field errors (camelCase names), empty when the values are fine.</summary>
    public Dictionary<string, string[]> Validate(RequestSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = new Dictionary<string, string[]>();
        void Profile(string field, int? id)
        {
            if (id is { } value && profiles is not null && !profiles.Exists(value))
            {
                errors[field] = ["This quality profile does not exist."];
            }
        }

        void Root(string field, int? id, MediaType type)
        {
            if (id is not { } value || rootFolders is null)
            {
                return;
            }

            if (rootFolders.Find(value) is not { } root)
            {
                errors[field] = ["This root folder does not exist."];
            }
            else if (root.MediaType != type)
            {
                errors[field] = [type == MediaType.Movie ? "This is a root folder for series." : "This is a root folder for movies."];
            }
        }

        void Limit(string field, int? limit)
        {
            if (limit is < 0 or > MaxLimit)
            {
                errors[field] = [$"Use 0 to {MaxLimit}, or leave it empty for no limit."];
            }
        }

        Profile("defaultMovieProfileId", values.DefaultMovieProfileId);
        Profile("defaultSeriesProfileId", values.DefaultSeriesProfileId);
        Root("defaultMovieRootFolderId", values.DefaultMovieRootFolderId, MediaType.Movie);
        Root("defaultSeriesRootFolderId", values.DefaultSeriesRootFolderId, MediaType.Series);
        Limit("defaultMovieLimit", values.DefaultMovieLimit);
        Limit("defaultSeriesLimit", values.DefaultSeriesLimit);
        if (values.LimitDays is < 1 or > MaxLimitDays)
        {
            errors["limitDays"] = [$"Use 1 to {MaxLimitDays} days."];
        }

        if (!RequestSeriesMonitor.Values.Contains(values.SeriesMonitor))
        {
            errors["seriesMonitor"] = [$"Use one of: {string.Join(", ", RequestSeriesMonitor.Values)}."];
        }

        return errors;
    }

    private bool Bool(string key, bool fallback) =>
        config.Get(key) is { Length: > 0 } text && bool.TryParse(text, out var value) ? value : fallback;

    private int? Int(string key) =>
        int.TryParse(config.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Text(bool value) => value ? "true" : "false";

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
