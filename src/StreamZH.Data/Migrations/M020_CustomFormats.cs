// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>Custom formats (0.8) and their scores in the quality profiles.</summary>
[Migration(20, "Custom formats")]
public sealed class M020_CustomFormats : Migration
{
    public override void Up()
    {
        Create.Table("CustomFormats")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("IncludeInRename").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Specifications").AsString().NotNullable()
            .WithColumn("TrashId").AsString(64).Nullable();

        Create.Index("IX_CustomFormats_Name").OnTable("CustomFormats")
            .OnColumn("Name").Ascending().OnColumn("MediaType").Ascending().WithOptions().Unique();
        Create.Index("IX_CustomFormats_TrashId").OnTable("CustomFormats").OnColumn("TrashId").Ascending();

        Alter.Table("QualityProfiles")
            .AddColumn("FormatItems").AsString().NotNullable().WithDefaultValue("[]")
            .AddColumn("MinFormatScore").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("CutoffFormatScore").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("MinUpgradeFormatScore").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("TrashMovieTemplate").AsString(64).Nullable()
            .AddColumn("TrashSeriesTemplate").AsString(64).Nullable();
    }

    public override void Down()
    {
        Delete.Table("CustomFormats");
    }
}
