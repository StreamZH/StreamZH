// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Core.Decisions.Specifications;

/// <summary>
/// The release must contain the profile language: Any accepts everything, Original needs the original
/// language of the movie or series, a specific language needs that language.
/// A release without a parsed language counts as English.
/// </summary>
public sealed class LanguageSpecification : IDecisionSpecification
{
    public Rejection? Evaluate(ReleaseCandidate candidate, DecisionContext context)
    {
        var wanted = Language.FindById(context.Profile.LanguageId);

        if (wanted == Language.Any)
        {
            return null;
        }

        var languages = LanguageRules.EffectiveLanguages(candidate.Release);

        if (wanted == Language.Original)
        {
            var original = context.Target.OriginalLanguage;

            // Without a known original language there is nothing to check against.
            if (!LanguageRules.IsReal(original) || languages.Contains(original))
            {
                return null;
            }

            return new Rejection(
                RejectionCodes.WantedLanguage,
                $"Original language ({original.Name}) is wanted, but found {LanguageRules.Describe(languages)}");
        }

        if (languages.Contains(wanted))
        {
            return null;
        }

        return new Rejection(
            RejectionCodes.WantedLanguage,
            $"{wanted.Name} is wanted, but found {LanguageRules.Describe(languages)}");
    }
}
