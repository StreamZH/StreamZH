// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core.Profiles;

namespace StreamZH.Data;

/// <summary>TRaSH sync state per quality profile: the options (table TrashProfiles) and the user's score overrides.</summary>
public sealed class TrashRepository(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TrashProfileOptions? Options(int profileId)
    {
        using var connection = database.Open();
        var json = connection.QuerySingleOrDefault<string>("SELECT Options FROM TrashProfiles WHERE ProfileId = @profileId", new { profileId });
        return json is null ? null : JsonSerializer.Deserialize<TrashProfileOptions>(json, Json) ?? new TrashProfileOptions();
    }

    public void SaveOptions(int profileId, TrashProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO TrashProfiles (ProfileId, Options) VALUES (@profileId, @json)
            ON CONFLICT(ProfileId) DO UPDATE SET Options = excluded.Options
            """,
            new { profileId, json = JsonSerializer.Serialize(options, Json) });
    }

    /// <summary>Custom format id → score the user set on this profile.</summary>
    public IReadOnlyDictionary<int, int> Overrides(int profileId)
    {
        using var connection = database.Open();
        return connection.Query<(long CustomFormatId, long Score)>(
                "SELECT CustomFormatId, Score FROM TrashScoreOverrides WHERE ProfileId = @profileId", new { profileId })
            .ToDictionary(r => (int)r.CustomFormatId, r => (int)r.Score);
    }

    public void SetOverride(int profileId, int customFormatId, int score)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO TrashScoreOverrides (ProfileId, CustomFormatId, Score) VALUES (@profileId, @customFormatId, @score)
            ON CONFLICT(ProfileId, CustomFormatId) DO UPDATE SET Score = excluded.Score
            """,
            new { profileId, customFormatId, score });
    }

    public void RemoveOverride(int profileId, int customFormatId)
    {
        using var connection = database.Open();
        connection.Execute(
            "DELETE FROM TrashScoreOverrides WHERE ProfileId = @profileId AND CustomFormatId = @customFormatId", new { profileId, customFormatId });
    }

    public void ClearOverrides(int profileId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM TrashScoreOverrides WHERE ProfileId = @profileId", new { profileId });
    }

    /// <summary>Forgets everything about the profile (deleted, or no longer following a template).</summary>
    public void Remove(int profileId)
    {
        using var connection = database.Open();
        connection.Execute(
            "DELETE FROM TrashScoreOverrides WHERE ProfileId = @profileId; DELETE FROM TrashProfiles WHERE ProfileId = @profileId", new { profileId });
    }
}
