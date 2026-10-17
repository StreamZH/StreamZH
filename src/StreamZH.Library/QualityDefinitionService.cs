// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;

namespace StreamZH.Library;

/// <summary>Size limits per quality (MB per minute of runtime).</summary>
public sealed class QualityDefinitionService(QualityDefinitionRepository definitions, ILogger<QualityDefinitionService> logger)
{
    public const int MaxTitleLength = 100;

    public IReadOnlyList<QualityDefinition> All() => definitions.All();

    /// <summary>Validates and saves several definitions at once. A blank title keeps the stored one.</summary>
    public IReadOnlyList<QualityDefinition> Save(IReadOnlyList<QualityDefinition> changes)
    {
        var existing = definitions.All().ToDictionary(d => d.QualityId);
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<int>();
        var result = new List<QualityDefinition>();

        for (var i = 0; i < changes.Count; i++)
        {
            var d = changes[i];
            var key = $"[{i}]";

            if (Quality.All.All(q => q.Id != d.QualityId))
            {
                errors[$"{key}.qualityId"] = [$"Unknown quality id {d.QualityId}."];
                continue;
            }

            if (!seen.Add(d.QualityId))
            {
                errors[$"{key}.qualityId"] = ["Each quality may only appear once."];
                continue;
            }

            if (d.MinSize < 0)
            {
                errors[$"{key}.minSize"] = ["Must be 0 or more."];
            }

            if (d.MaxSize is { } max && max < d.MinSize)
            {
                errors[$"{key}.maxSize"] = ["Must be at least the minimum."];
            }

            if (d.PreferredSize is { } preferred && (preferred < d.MinSize || (d.MaxSize is { } m && preferred > m)))
            {
                errors[$"{key}.preferredSize"] = ["Must be between the minimum and the maximum."];
            }

            var title = string.IsNullOrWhiteSpace(d.Title)
                ? existing.GetValueOrDefault(d.QualityId)?.Title ?? Quality.FindById(d.QualityId).Name
                : d.Title.Trim();
            if (title.Length > MaxTitleLength)
            {
                errors[$"{key}.title"] = [$"Use at most {MaxTitleLength} characters."];
            }

            result.Add(d with { Title = title });
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        definitions.Save(result);
        logger.LogInformation("Quality size limits saved ({Count} qualities)", result.Count);
        return definitions.All();
    }
}
