// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Data;

namespace StreamZH.Library.Trash;

/// <param name="Enabled">The scheduled sync runs (and keeps formats and template profiles up to date).</param>
/// <param name="Repository">Git repository of the guides (GitHub URL), or a local folder with a copy of it.</param>
/// <param name="Branch">Branch whose archive is downloaded.</param>
/// <param name="SyncIntervalHours">Hours between two scheduled syncs.</param>
/// <param name="AutoUpdate">After a sync, managed formats and template profiles follow the guides.</param>
/// <param name="KeepScoreOverrides">Scores changed by hand on a template profile survive the sync.</param>
public sealed record TrashSettingsValues(
    bool Enabled, string Repository, string Branch, int SyncIntervalHours, bool AutoUpdate, bool KeepScoreOverrides);

/// <summary>Settings → Profiles → TRaSH Guides, read from the config table on every use.</summary>
public sealed class TrashSettings(ConfigRepository config)
{
    public const string DefaultRepository = "https://github.com/TRaSH-Guides/Guides";
    public const string DefaultBranch = "master";
    public const int DefaultSyncIntervalHours = 24;
    public const int MinSyncIntervalHours = 1;
    public const int MaxSyncIntervalHours = 720;

    public static readonly TrashSettingsValues Defaults =
        new(true, DefaultRepository, DefaultBranch, DefaultSyncIntervalHours, AutoUpdate: true, KeepScoreOverrides: true);

    public TrashSettingsValues Values => new(
        Bool(ConfigKeys.TrashEnabled, Defaults.Enabled),
        config.Get(ConfigKeys.TrashRepository) is { Length: > 0 } repository ? repository : DefaultRepository,
        config.Get(ConfigKeys.TrashBranch) is { Length: > 0 } branch ? branch : DefaultBranch,
        int.TryParse(config.Get(ConfigKeys.TrashSyncIntervalHours), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            ? Math.Clamp(hours, MinSyncIntervalHours, MaxSyncIntervalHours)
            : DefaultSyncIntervalHours,
        Bool(ConfigKeys.TrashAutoUpdate, Defaults.AutoUpdate),
        Bool(ConfigKeys.TrashKeepScoreOverrides, Defaults.KeepScoreOverrides));

    /// <summary>Saves values that passed <see cref="Validate"/>; the repository is stored without a trailing slash or ".git".</summary>
    public void Save(TrashSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        config.Set(ConfigKeys.TrashEnabled, values.Enabled ? "true" : "false");
        config.Set(ConfigKeys.TrashRepository, NormalizeRepository(values.Repository));
        config.Set(ConfigKeys.TrashBranch, values.Branch.Trim());
        config.Set(ConfigKeys.TrashSyncIntervalHours, values.SyncIntervalHours.ToString(CultureInfo.InvariantCulture));
        config.Set(ConfigKeys.TrashAutoUpdate, values.AutoUpdate ? "true" : "false");
        config.Set(ConfigKeys.TrashKeepScoreOverrides, values.KeepScoreOverrides ? "true" : "false");
    }

    /// <summary>Field errors (camelCase); empty when the values are fine.</summary>
    public static Dictionary<string, string[]> Validate(TrashSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var errors = new Dictionary<string, string[]>();
        var repository = values.Repository?.Trim() ?? string.Empty;
        if (repository.Length == 0)
        {
            errors["repository"] = [$"Enter the repository, e.g. {DefaultRepository}."];
        }
        else if (Path.IsPathFullyQualified(repository))
        {
            if (!File.Exists(Path.Combine(repository, TrashGuideParser.MetadataFile)))
            {
                errors["repository"] = [$"No {TrashGuideParser.MetadataFile} in this folder; use a copy of the guides repository."];
            }
        }
        else if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                 || uri.AbsolutePath.Trim('/').Length == 0)
        {
            errors["repository"] = ["Use the address of a git repository, e.g. https://github.com/TRaSH-Guides/Guides."];
        }

        var branch = values.Branch?.Trim() ?? string.Empty;
        if (branch.Length == 0 || branch.Length > 100 || branch.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            || branch.Contains("..", StringComparison.Ordinal))
        {
            errors["branch"] = ["Enter a branch name, e.g. master."];
        }

        if (values.SyncIntervalHours is < MinSyncIntervalHours or > MaxSyncIntervalHours)
        {
            errors["syncIntervalHours"] = [$"Use {MinSyncIntervalHours} to {MaxSyncIntervalHours} hours."];
        }

        return errors;
    }

    public static string NormalizeRepository(string repository)
    {
        var text = repository.Trim();
        if (Path.IsPathFullyQualified(text))
        {
            return Path.TrimEndingDirectorySeparator(text);
        }

        text = text.TrimEnd('/');
        return text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
    }

    private bool Bool(string key, bool fallback) => config.Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };
}
