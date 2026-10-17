// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// What happened to releases: grabbed now, imported/failed/deleted from 0.6 on. No foreign keys on purpose:
/// history stays when a movie or series is removed from the library (like Radarr/Sonarr).
/// </summary>
[Migration(6, "History of grabbed releases")]
public sealed class M006_History : Migration
{
    public override void Up()
    {
        Create.Table("History")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Date").AsDateTime().NotNullable().Indexed()
            .WithColumn("EventType").AsInt32().NotNullable()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("MovieId").AsInt32().Nullable().Indexed()
            .WithColumn("SeriesId").AsInt32().Nullable().Indexed()
            .WithColumn("EpisodeIds").AsString().Nullable()
            .WithColumn("SourceTitle").AsString().NotNullable()
            .WithColumn("QualityId").AsInt32().NotNullable()
            .WithColumn("Languages").AsString().NotNullable()
            .WithColumn("ReleaseGroup").AsString().Nullable()
            .WithColumn("Indexer").AsString().Nullable()
            .WithColumn("IndexerId").AsInt32().Nullable()
            .WithColumn("Size").AsInt64().NotNullable()
            .WithColumn("Data").AsString().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("History");
    }
}
