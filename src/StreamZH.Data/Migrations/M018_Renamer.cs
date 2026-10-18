// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Tools → File renamer: every run with its old and new paths (for the history and Undo), and saved presets
/// (filter, rules and options as JSON).
/// </summary>
[Migration(18, "File renamer runs and presets")]
public sealed class M018_Renamer : Migration
{
    public override void Up()
    {
        Create.Table("RenamerRuns")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Started").AsDateTime().NotNullable()
            .WithColumn("Finished").AsDateTime().Nullable()
            .WithColumn("Folder").AsString().NotNullable()
            .WithColumn("State").AsInt32().NotNullable()
            .WithColumn("Total").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Done").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Failed").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Error").AsString().Nullable()
            .WithColumn("Summary").AsString().NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("Rules").AsString().NotNullable().WithDefaultValue("[]")
            .WithColumn("UndoneAt").AsDateTime().Nullable();

        Create.Table("RenamerRunItems")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("RunId").AsInt32().NotNullable().ForeignKey("RenamerRuns", "Id").OnDelete(System.Data.Rule.Cascade).Indexed()
            .WithColumn("Ord").AsInt32().NotNullable()
            .WithColumn("Kind").AsInt32().NotNullable()
            .WithColumn("OldPath").AsString().NotNullable()
            .WithColumn("NewPath").AsString().NotNullable()
            .WithColumn("IsFolder").AsBoolean().NotNullable()
            .WithColumn("State").AsInt32().NotNullable()
            .WithColumn("Error").AsString().Nullable();

        Create.Table("RenamerPresets")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Content").AsString().NotNullable()
            .WithColumn("Updated").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("RenamerPresets");
        Delete.Table("RenamerRunItems");
        Delete.Table("RenamerRuns");
    }
}
