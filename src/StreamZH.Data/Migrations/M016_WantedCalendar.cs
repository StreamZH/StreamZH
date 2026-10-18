// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Wanted and Calendar (like Radarr/Sonarr): when a movie or episode was last searched by an automatic search, and the
/// digital and physical release dates of movies from TMDB (for the calendar and the "Released" minimum availability).
/// </summary>
[Migration(16, "Last searched and movie release dates")]
public sealed class M016_WantedCalendar : Migration
{
    public override void Up()
    {
        // MediaType 1 = movie (ItemId = movie id), 2 = episode (ItemId = episode id).
        Create.Table("LastSearches")
            .WithColumn("MediaType").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("ItemId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("Time").AsDateTime().NotNullable();

        Alter.Table("Movies").AddColumn("DigitalRelease").AsString(10).Nullable();
        Alter.Table("Movies").AddColumn("PhysicalRelease").AsString(10).Nullable();
    }

    public override void Down()
    {
        Delete.Column("PhysicalRelease").FromTable("Movies");
        Delete.Column("DigitalRelease").FromTable("Movies");
        Delete.Table("LastSearches");
    }
}
