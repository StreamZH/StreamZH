// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Quality profiles (/qualityprofile; Sonarr v3 also /languageprofile). StreamZH uses one profile for movies and series,
/// so a profile is matched by name: Radarr brings the movie format scores, Sonarr the episode format scores, and taking
/// over Sonarr after Radarr updates the same profile. A profile that holds only one app's part (or a new one) takes that
/// app's qualities, cutoff, language and score limits; once it holds both parts, the qualities either app allows are all
/// allowed and the rest stays, with a warning where the apps differ.
/// </summary>
public sealed class QualityProfilesSection(string source) : ArrSettingsSection(source)
{
    public const string IdKind = "qualityProfile";

    public override string Key => "qualityProfiles";

    public override string Label => "Quality profiles";

    public override string Description =>
        "Qualities, cutoff, upgrades and custom format scores. StreamZH uses one profile for movies and series: a Radarr and a Sonarr " +
        "profile with the same name become one (movie scores from Radarr, episode scores from Sonarr).";

    public override int Order => 250;

    protected override async Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken)
    {
        var plan = new SettingsPlan();
        var profiles = await reader.GetListAsync("/qualityprofile", cancellationToken);
        var oldFormats = reader.IsSonarrV3
            ? new Dictionary<int, string>()
            : (await reader.GetListAsync("/customformat", cancellationToken))
                .Where(f => ArrJson.Int(f, "id") is not null)
                .GroupBy(f => ArrJson.Int(f, "id")!.Value)
                .ToDictionary(g => g.Key, g => ArrJson.Str(g.First(), "name") ?? string.Empty);
        var languageProfiles = reader.IsSonarrV3 ? await reader.GetListAsync("/languageprofile", cancellationToken) : [];

        var formats = reader.Service<CustomFormatRepository>().All();
        var formatTypes = formats.ToDictionary(f => f.Id, f => f.MediaType);
        var formatsByName = formats.Where(f => f.MediaType == reader.MediaType)
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var repository = reader.Service<QualityProfileRepository>();
        var other = IsRadarr ? "Sonarr" : "Radarr";

        foreach (var profile in profiles)
        {
            var id = ArrJson.Int(profile, "id") ?? 0;
            var key = ItemKey(id);
            var name = ArrJson.Str(profile, "name")?.Trim() ?? string.Empty;
            var label = name.Length > 0 ? name : $"Profile {id}";
            if (name.Length == 0)
            {
                plan.Skip(key, label, "The profile has no name.");
                continue;
            }

            var warnings = new List<string>();
            var qualities = ArrProfiles.Qualities(Source, profile);
            warnings.AddRange(qualities.Warnings);
            if (!qualities.Items.Any(i => i.Allowed))
            {
                plan.Skip(key, label, "StreamZH knows none of the qualities this profile allows.");
                continue;
            }

            // Custom format scores, mapped to StreamZH's formats of this media type.
            var scores = new List<ProfileFormatItem>();
            var pending = 0;
            foreach (var item in ArrJson.Array(profile, "formatItems"))
            {
                var score = ArrJson.Int(item, "score") ?? 0;
                var formatId = ArrJson.Int(item, "format") ?? 0;
                if (score == 0)
                {
                    continue;
                }

                var formatName = ArrJson.Str(item, "name") ?? oldFormats.GetValueOrDefault(formatId) ?? string.Empty;
                var mapped = reader.Context.Ids.Get(CustomFormatsSection.IdKind, formatId)
                    ?? (formatsByName.TryGetValue(formatName, out var byName) ? byName : null);
                if (mapped is { } newId && scores.All(s => s.CustomFormatId != newId))
                {
                    scores.Add(new ProfileFormatItem(newId, Math.Clamp(score, -QualityProfileService.MaxFormatScore, QualityProfileService.MaxFormatScore)));
                }
                else if (mapped is null && !reader.Running && oldFormats.ContainsKey(formatId))
                {
                    // Comes with the custom formats part of this migration.
                    pending++;
                }
                else if (mapped is null)
                {
                    warnings.Add($"The score {score} for '{formatName}' is left out: StreamZH has no {(IsRadarr ? "movie" : "series")} format of that name. Take over the custom formats first.");
                }
            }

            // Language: Radarr's profile language; Sonarr v3 a language profile; Sonarr v4 none (languages are custom formats there).
            Language? language = null;
            bool? dual = null;
            if (IsRadarr)
            {
                language = ArrJson.Get(profile, "language") is { } l ? ArrQualities.MapLanguage(Source, l) : null;
                language ??= Language.Any;
            }
            else if (reader.IsSonarrV3 && languageProfiles.Count > 0)
            {
                var languageProfile = languageProfiles.FirstOrDefault(p => string.Equals(ArrJson.Str(p, "name")?.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (languageProfile.ValueKind == JsonValueKind.Undefined)
                {
                    languageProfile = languageProfiles[0];
                    if (languageProfiles.Count > 1)
                    {
                        warnings.Add($"Sonarr has several language profiles; StreamZH uses '{ArrJson.Str(languageProfile, "name")}' for this profile.");
                    }
                }

                var choice = ArrProfiles.LanguageProfile(languageProfile);
                language = choice.Language;
                dual = choice.PreferDualLanguage;
                if (choice.Warning is { } languageWarning)
                {
                    warnings.Add(languageWarning);
                }
            }

            var minUpgrade = ArrJson.Int(profile, "minUpgradeFormatScore") is { } up && up >= 1 ? up : 1;
            var incoming = new QualityProfile
            {
                Name = name,
                Items = qualities.Items,
                CutoffQualityId = qualities.CutoffQualityId,
                UpgradeAllowed = ArrJson.Bool(profile, "upgradeAllowed", true),
                LanguageId = (language ?? Language.Any).Id,
                PreferDualLanguage = dual ?? false,
                FormatItems = scores,
                MinFormatScore = Clamp(ArrJson.Int(profile, "minFormatScore") ?? 0),
                CutoffFormatScore = Clamp(ArrJson.Int(profile, "cutoffFormatScore") ?? 0),
                MinUpgradeFormatScore = Math.Min(minUpgrade, QualityProfileService.MaxFormatScore),
            };

            var formatCount = scores.Count + pending;
            var detail = $"{(language is null ? string.Empty : language.Name + " · ")}{qualities.Items.Count(i => i.Allowed)} qualities · cutoff {Quality.FindById(incoming.CutoffQualityId).Name} · {formatCount} {(formatCount == 1 ? "format" : "formats")}";
            var existing = repository.FindByName(name);
            if (existing is null)
            {
                if (!IsRadarr && !reader.IsSonarrV3)
                {
                    warnings.Add("Sonarr v4 handles languages with custom formats; the profile accepts any language like in Sonarr.");
                }

                plan.Add(new MigrationItem(key, label, MigrationAction.Create, detail, Warning: Join(warnings)), _ =>
                {
                    var created = reader.Service<QualityProfileService>().Create(incoming);
                    reader.Context.Ids.Set(IdKind, id, created.Id);
                });
                continue;
            }

            var existingId = existing.Id;
            var merged = ArrProfiles.HasOtherSide(existing, formatTypes, reader.MediaType);
            var target = merged
                ? existing with
                {
                    Name = name,
                    Items = ArrProfiles.UnionAllowed(existing.Items, incoming.Items),
                    FormatItems = ArrProfiles.ReplaceScores(existing.FormatItems, scores, formatTypes, reader.MediaType),
                }
                : existing with
                {
                    Name = name,
                    Items = incoming.Items,
                    CutoffQualityId = incoming.CutoffQualityId,
                    UpgradeAllowed = incoming.UpgradeAllowed,
                    LanguageId = language?.Id ?? existing.LanguageId,
                    PreferDualLanguage = dual ?? existing.PreferDualLanguage,
                    FormatItems = ArrProfiles.ReplaceScores(existing.FormatItems, scores, formatTypes, reader.MediaType),
                    MinFormatScore = incoming.MinFormatScore,
                    CutoffFormatScore = incoming.CutoffFormatScore,
                    MinUpgradeFormatScore = incoming.MinUpgradeFormatScore,
                };

            if (merged)
            {
                detail += $" · joins the {other} part";
                warnings.AddRange(Differences(existing, incoming, target, language, other));
            }

            if ((IsRadarr ? existing.TrashMovieTemplate : existing.TrashSeriesTemplate) is { } template)
            {
                warnings.Add($"In StreamZH this profile follows the TRaSH Guides template '{template}'; the next TRaSH sync may change it again.");
            }

            if (ArrProfiles.Equivalent(target, existing))
            {
                plan.Add(new MigrationItem(key, label, MigrationAction.Link, detail, "Already the same.", Join(warnings)), _ => reader.Context.Ids.Set(IdKind, id, existingId));
                continue;
            }

            plan.Add(new MigrationItem(key, label, MigrationAction.Update, detail, Warning: Join(warnings)), _ =>
            {
                reader.Service<QualityProfileService>().Update(existingId, target);
                reader.Context.Ids.Set(IdKind, id, existingId);
            });
        }

        return plan;
    }

    /// <summary>What stays as it is when the profile already holds the other app's part.</summary>
    private static IEnumerable<string> Differences(QualityProfile existing, QualityProfile incoming, QualityProfile target, Language? language, string other)
    {
        var added = target.Items.Where(t => t.Allowed && !existing.IsAllowed(t.QualityId)).Select(t => Quality.FindById(t.QualityId).Name).ToList();
        if (added.Count > 0)
        {
            yield return $"Allows {string.Join(", ", added)} too, so the profile covers what Radarr and Sonarr allowed.";
        }

        if (incoming.CutoffQualityId != existing.CutoffQualityId)
        {
            yield return $"The cutoff stays {Quality.FindById(existing.CutoffQualityId).Name} (from {other}); this app has {Quality.FindById(incoming.CutoffQualityId).Name}.";
        }

        if (language is not null && language.Id != existing.LanguageId)
        {
            yield return $"The language stays {Language.FindById(existing.LanguageId).Name} (from {other}); this app has {language.Name}.";
        }

        if (incoming.MinFormatScore != existing.MinFormatScore || incoming.CutoffFormatScore != existing.CutoffFormatScore
            || incoming.MinUpgradeFormatScore != existing.MinUpgradeFormatScore || incoming.UpgradeAllowed != existing.UpgradeAllowed)
        {
            yield return $"Upgrades and the minimum, cutoff and upgrade scores stay as {other} set them.";
        }
    }

    private static int Clamp(int score) => Math.Clamp(score, -QualityProfileService.MaxFormatScore, QualityProfileService.MaxFormatScore);

    private static string? Join(List<string> warnings) => warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct());
}
