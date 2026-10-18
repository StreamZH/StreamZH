// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library;

/// <summary>Input failed validation; <see cref="Errors"/> maps camelCase field names to messages.</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more fields are invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>
/// Creates, validates and removes quality profiles and keeps track of the default profile. Custom format scores of
/// formats that no longer exist are dropped when a profile is saved; only non-zero scores are stored.
/// </summary>
public sealed class QualityProfileService(
    QualityProfileRepository profiles,
    ConfigRepository config,
    ILogger<QualityProfileService> logger,
    CustomFormatRepository? customFormats = null,
    IEnumerable<IQualityProfileObserver>? observers = null)
{
    public const int MaxNameLength = 100;

    /// <summary>Custom format scores (and the minimum, cutoff and upgrade step) must lie within ±this value, like Radarr's UI.</summary>
    public const int MaxFormatScore = 1_000_000;

    private static readonly IReadOnlySet<int> KnownQualityIds = Quality.All.Select(q => q.Id).ToHashSet();

    public IReadOnlyList<QualityProfile> All() => profiles.All();

    public QualityProfile? Find(int id) => profiles.Find(id);

    /// <summary>A new profile to start from: every quality, none allowed.</summary>
    public static QualityProfile Schema() => new()
    {
        Name = string.Empty,
        Items = QualityDefaults.Items([]),
        CutoffQualityId = Quality.Unknown.Id,
        UpgradeAllowed = true,
        LanguageId = Language.Original.Id,
        PreferDualLanguage = false,
    };

    /// <summary>The profile used when nothing else is chosen. Falls back to the first profile if the setting points nowhere.</summary>
    public int DefaultProfileId()
    {
        if (int.TryParse(config.Get(ConfigKeys.DefaultQualityProfileId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && profiles.Exists(id))
        {
            return id;
        }

        return profiles.FirstId() ?? 0;
    }

    public void SetDefault(int id)
    {
        if (!profiles.Exists(id))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["defaultQualityProfileId"] = ["This quality profile does not exist."] });
        }

        config.Set(ConfigKeys.DefaultQualityProfileId, id.ToString(CultureInfo.InvariantCulture));
        logger.LogInformation("Default quality profile set to {Id}", id);
    }

    /// <summary>The requested profile, or the default one if none was requested.</summary>
    public int Resolve(int? requested)
    {
        if (requested is null)
        {
            return DefaultProfileId();
        }

        return profiles.Exists(requested.Value)
            ? requested.Value
            : throw new LibraryException("Quality profile not found.", LibraryError.Invalid);
    }

    public QualityProfile Create(QualityProfile profile)
    {
        var normalized = Normalize(profile) with { Id = 0 };
        Validate(normalized, null);
        var id = profiles.Add(normalized);
        logger.LogInformation("Quality profile '{Name}' created", normalized.Name);
        return profiles.Find(id)!;
    }

    public QualityProfile Update(int id, QualityProfile profile)
    {
        var before = profiles.Find(id) ?? throw new LibraryException("Quality profile not found.", LibraryError.NotFound);
        var normalized = Normalize(profile) with { Id = id };
        Validate(normalized, id);
        profiles.Update(normalized);
        logger.LogInformation("Quality profile '{Name}' saved", normalized.Name);
        var saved = profiles.Find(id)!;
        foreach (var observer in observers ?? [])
        {
            observer.Updated(before, saved);
        }

        return saved;
    }

    public void Delete(int id)
    {
        var profile = profiles.Find(id) ?? throw new LibraryException("Quality profile not found.", LibraryError.NotFound);

        var used = profiles.UsageCount(id);
        if (used > 0)
        {
            throw new LibraryException(
                $"'{profile.Name}' is used by {used} {(used == 1 ? "movie or series" : "movies and series")}. Assign another profile first.",
                LibraryError.InUse);
        }

        if (profiles.Count() <= 1)
        {
            throw new LibraryException("The last quality profile cannot be deleted.", LibraryError.Conflict);
        }

        var wasDefault = DefaultProfileId() == id;
        profiles.Delete(id);
        logger.LogInformation("Quality profile '{Name}' deleted", profile.Name);
        foreach (var observer in observers ?? [])
        {
            observer.Deleted(id);
        }

        if (wasDefault && profiles.FirstId() is { } next)
        {
            SetDefault(next);
        }
    }

    /// <summary>Checks a profile and throws <see cref="ValidationFailedException"/> with all problems found.</summary>
    public void Validate(QualityProfile profile, int? existingId)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors["name"] = ["Enter a name."];
        }
        else if (profile.Name.Length > MaxNameLength)
        {
            errors["name"] = [$"Use at most {MaxNameLength} characters."];
        }
        else if (profiles.FindByName(profile.Name) is { } other && other.Id != existingId)
        {
            errors["name"] = ["A profile with this name already exists."];
        }

        var items = profile.Items;
        var unknown = items.Where(i => !KnownQualityIds.Contains(i.QualityId)).Select(i => i.QualityId).Distinct().ToList();
        if (unknown.Count > 0)
        {
            errors["items"] = [$"Unknown quality id: {string.Join(", ", unknown)}."];
        }
        else if (items.GroupBy(i => i.QualityId).Any(g => g.Count() > 1))
        {
            errors["items"] = ["Each quality may only appear once."];
        }
        else if (!items.Any(i => i.Allowed))
        {
            errors["items"] = ["Allow at least one quality."];
        }

        if (!profile.IsAllowed(profile.CutoffQualityId))
        {
            errors["cutoffQualityId"] = ["The cutoff must be one of the allowed qualities."];
        }

        if (!IsValidLanguage(profile.LanguageId))
        {
            errors["languageId"] = ["Unknown language."];
        }

        ValidateGroups(profile, errors);
        ValidateFormats(profile, errors);

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    /// <summary>Real languages plus the Any and Original settings; Unknown is not a valid choice.</summary>
    public static bool IsValidLanguage(int languageId) =>
        languageId == Language.Any.Id || languageId == Language.Original.Id || (languageId > 0 && Language.FindById(languageId).Id == languageId);

    private static void ValidateGroups(QualityProfile profile, Dictionary<string, string[]> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profile.Items.Count; i++)
        {
            var group = profile.Items[i].Group;
            if (group is null)
            {
                continue;
            }

            if (group.Length > MaxNameLength)
            {
                errors[$"items[{i}].group"] = [$"Use at most {MaxNameLength} characters."];
            }
            else if ((i == 0 || profile.Items[i - 1].Group != group) && !seen.Add(group))
            {
                errors[$"items[{i}].group"] = ["The qualities of a group must be next to each other."];
            }
        }
    }

    private void ValidateFormats(QualityProfile profile, Dictionary<string, string[]> errors)
    {
        var known = customFormats?.All().Select(f => f.Id).ToHashSet();
        var seen = new HashSet<int>();
        for (var i = 0; i < profile.FormatItems.Count; i++)
        {
            var item = profile.FormatItems[i];
            if (known is not null && !known.Contains(item.CustomFormatId))
            {
                errors[$"formatItems[{i}].customFormatId"] = [$"Unknown custom format id: {item.CustomFormatId}."];
            }
            else if (!seen.Add(item.CustomFormatId))
            {
                errors[$"formatItems[{i}].customFormatId"] = ["Each custom format may only appear once."];
            }

            if (Math.Abs((long)item.Score) > MaxFormatScore)
            {
                errors[$"formatItems[{i}].score"] = [OutOfRange];
            }
        }

        if (Math.Abs((long)profile.MinFormatScore) > MaxFormatScore)
        {
            errors["minFormatScore"] = [OutOfRange];
        }

        if (Math.Abs((long)profile.CutoffFormatScore) > MaxFormatScore)
        {
            errors["cutoffFormatScore"] = [OutOfRange];
        }

        if (profile.MinUpgradeFormatScore < 1 || profile.MinUpgradeFormatScore > MaxFormatScore)
        {
            errors["minUpgradeFormatScore"] = ["Use a value from 1 to 1000000."];
        }
    }

    private const string OutOfRange = "Use a value from -1000000 to 1000000.";

    private QualityProfile Normalize(QualityProfile profile)
    {
        var items = profile.Items ?? [];
        var formatItems = profile.FormatItems ?? [];
        return profile with
        {
            Name = profile.Name?.Trim() ?? string.Empty,
            Items = [.. items.Select(i => i with { Group = string.IsNullOrWhiteSpace(i.Group) ? null : i.Group.Trim() })],
            FormatItems = [.. formatItems.Where(i => i.Score != 0)],
            TrashMovieTemplate = string.IsNullOrWhiteSpace(profile.TrashMovieTemplate) ? null : profile.TrashMovieTemplate.Trim(),
            TrashSeriesTemplate = string.IsNullOrWhiteSpace(profile.TrashSeriesTemplate) ? null : profile.TrashSeriesTemplate.Trim(),
        };
    }

    /// <summary>The stored scores without those of formats that no longer exist (they may have been deleted meanwhile).</summary>
    public IReadOnlyList<ProfileFormatItem> ExistingFormatItems(QualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (customFormats is null)
        {
            return profile.FormatItems;
        }

        var known = customFormats.All().Select(f => f.Id).ToHashSet();
        return [.. profile.FormatItems.Where(i => known.Contains(i.CustomFormatId))];
    }
}
