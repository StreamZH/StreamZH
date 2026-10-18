// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// TRaSH Guides sync (0.8): per quality profile that follows a TRaSH template the sync options (extra CF groups, explicit
/// formats and field overrides as JSON) and the custom format scores the user changed, which the sync keeps.
/// </summary>
[Migration(21, "TRaSH Guides sync")]
public sealed class M021_TrashGuides : Migration
{
    public override void Up()
    {
        Create.Table("TrashProfiles")
            .WithColumn("ProfileId").AsInt32().PrimaryKey()
            .WithColumn("Options").AsString().NotNullable().WithDefaultValue("{}");

        Create.Table("TrashScoreOverrides")
            .WithColumn("ProfileId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("CustomFormatId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("Score").AsInt32().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("TrashScoreOverrides");
        Delete.Table("TrashProfiles");
    }
}
