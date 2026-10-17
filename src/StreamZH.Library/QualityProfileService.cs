// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
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

/// <summary>Creates, validates and removes quality profiles and keeps track of the default profile.</summary>
public sealed class QualityProfileService(QualityProfileRepository profiles, ConfigRepository config, ILogger<QualityProfileService> logger)
{
    public const int MaxNameLength = 100;

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
        var normalized = Normalize(profile);
        Validate(normalized, null);
        var id = profiles.Add(normalized);
        logger.LogInformation("Quality profile '{Name}' created", normalized.Name);
        return profiles.Find(id)!;
    }

    public QualityProfile Update(int id, QualityProfile profile)
    {
        _ = profiles.Find(id) ?? throw new LibraryException("Quality profile not found.", LibraryError.NotFound);
        var normalized = Normalize(profile) with { Id = id };
        Validate(normalized, id);
        profiles.Update(normalized);
        logger.LogInformation("Quality profile '{Name}' saved", normalized.Name);
        return profiles.Find(id)!;
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

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    /// <summary>Real languages plus the Any and Original settings; Unknown is not a valid choice.</summary>
    public static bool IsValidLanguage(int languageId) =>
        languageId == Language.Any.Id || languageId == Language.Original.Id || (languageId > 0 && Language.FindById(languageId).Id == languageId);

    private static QualityProfile Normalize(QualityProfile profile) => profile with
    {
        Name = profile.Name?.Trim() ?? string.Empty,
        Items = profile.Items ?? [],
    };
}
