// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Release profiles (Settings → Indexers, like Sonarr v3): must contain, must not contain and preferred terms, limited
/// to indexers and quality profiles (JSON lists, empty = all). Movies get the TMDB release date for the minimum availability.
/// </summary>
[Migration(13, "Release profiles and movie release date")]
public sealed class M013_ReleaseProfiles : Migration
{
    public override void Up()
    {
        Create.Table("ReleaseProfiles")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Required").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("Ignored").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("Preferred").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("IndexerIds").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("QualityProfileIds").AsString().NotNullable().WithDefaultValue("[]");

        Alter.Table("Movies").AddColumn("InCinemas").AsString(10).Nullable();
    }

    public override void Down()
    {
        Delete.Column("InCinemas").FromTable("Movies");
        Delete.Table("ReleaseProfiles");
    }
}
