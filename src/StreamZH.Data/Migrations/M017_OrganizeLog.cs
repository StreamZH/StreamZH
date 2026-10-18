// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// The rename log of "Organize" (library mass editor): one row per run and one per renamed movie or series with the old
/// and new folder and the renamed files (JSON), so the last run can be undone.
/// </summary>
[Migration(17, "Organize rename log")]
public sealed class M017_OrganizeLog : Migration
{
    public override void Up()
    {
        Create.Table("OrganizeRuns")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("Started").AsDateTime().NotNullable()
            .WithColumn("Finished").AsDateTime().Nullable()
            .WithColumn("Renamed").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Failed").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("UndoneAt").AsDateTime().Nullable();

        Create.Table("OrganizeRunItems")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("RunId").AsInt32().NotNullable().ForeignKey("FK_OrganizeRunItems_Run", "OrganizeRuns", "Id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("ItemId").AsInt32().NotNullable()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("OldFolder").AsString().NotNullable()
            .WithColumn("NewFolder").AsString().NotNullable()
            .WithColumn("Files").AsString().NotNullable().WithDefaultValue("[]");

        Create.Index("IX_OrganizeRunItems_RunId").OnTable("OrganizeRunItems").OnColumn("RunId");
    }

    public override void Down()
    {
        Delete.Table("OrganizeRunItems");
        Delete.Table("OrganizeRuns");
    }
}
