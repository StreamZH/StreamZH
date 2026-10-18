// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Profiles;
using StreamZH.Data;

namespace StreamZH.Library.Trash;

/// <summary>
/// Remembers what the user changes by hand on a profile that follows a TRaSH template, so the sync keeps it: changed
/// scores of formats the sync manages become score overrides, changed profile fields (qualities, cutoff, scores limits,
/// language) become field overrides. A profile that no longer follows a template forgets its TRaSH state.
/// </summary>
public sealed class TrashProfileObserver(TrashRepository trash) : IQualityProfileObserver
{
    public void Updated(QualityProfile before, QualityProfile after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (after.TrashMovieTemplate is null && after.TrashSeriesTemplate is null)
        {
            if (before.TrashMovieTemplate is not null || before.TrashSeriesTemplate is not null)
            {
                trash.Remove(after.Id);
            }

            return;
        }

        var options = trash.Options(after.Id) ?? new TrashProfileOptions();
        var managed = options.AppliedFormatIds.ToHashSet();
        var overrides = trash.Overrides(after.Id);
        var old = before.FormatItems.ToDictionary(i => i.CustomFormatId, i => i.Score);
        var now = after.FormatItems.ToDictionary(i => i.CustomFormatId, i => i.Score);
        foreach (var id in old.Keys.Union(now.Keys))
        {
            var oldScore = old.GetValueOrDefault(id);
            var newScore = now.GetValueOrDefault(id);
            if (oldScore != newScore && (managed.Contains(id) || overrides.ContainsKey(id)))
            {
                trash.SetOverride(after.Id, id, newScore);
            }
        }

        var changed = options with
        {
            Items = before.Items.SequenceEqual(after.Items) ? options.Items : after.Items,
            CutoffQualityId = before.CutoffQualityId == after.CutoffQualityId ? options.CutoffQualityId : after.CutoffQualityId,
            UpgradeAllowed = before.UpgradeAllowed == after.UpgradeAllowed ? options.UpgradeAllowed : after.UpgradeAllowed,
            MinFormatScore = before.MinFormatScore == after.MinFormatScore ? options.MinFormatScore : after.MinFormatScore,
            CutoffFormatScore = before.CutoffFormatScore == after.CutoffFormatScore ? options.CutoffFormatScore : after.CutoffFormatScore,
            MinUpgradeFormatScore = before.MinUpgradeFormatScore == after.MinUpgradeFormatScore ? options.MinUpgradeFormatScore : after.MinUpgradeFormatScore,
            LanguageId = before.LanguageId == after.LanguageId ? options.LanguageId : after.LanguageId,
        };

        // A changed quality list without a changed cutoff still needs the cutoff, or the template's could be missing from it.
        if (changed.Items is not null && changed.CutoffQualityId is null)
        {
            changed = changed with { CutoffQualityId = after.CutoffQualityId };
        }

        if (changed != options)
        {
            trash.SaveOptions(after.Id, changed);
        }
    }

    public void Deleted(int profileId) => trash.Remove(profileId);
}
