// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.8.1: alternative titles, series type and scene numbering, media info of files.</summary>
[Migration(22, "Alternative titles, series type, scene numbering, media info")]
public sealed class M022_TitlesNumberingMediaInfo : Migration
{
    public override void Up()
    {
        Create.Table("AlternativeTitles")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("ItemId").AsInt32().NotNullable()
            .WithColumn("Title").AsString(500).NotNullable()
            .WithColumn("CleanTitle").AsString(500).NotNullable()
            .WithColumn("Source").AsString(20).NotNullable()
            .WithColumn("LanguageId").AsInt32().Nullable()
            .WithColumn("SeasonNumber").AsInt32().Nullable()
            .WithColumn("SceneSeasonNumber").AsInt32().Nullable();
        Create.Index("IX_AlternativeTitles_Item").OnTable("AlternativeTitles")
            .OnColumn("MediaType").Ascending().OnColumn("ItemId").Ascending();
        Create.Index("IX_AlternativeTitles_CleanTitle").OnTable("AlternativeTitles").OnColumn("CleanTitle").Ascending();

        Alter.Table("Series").AddColumn("SeriesType").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("Episodes")
            .AddColumn("AbsoluteEpisodeNumber").AsInt32().Nullable()
            .AddColumn("SceneSeasonNumber").AsInt32().Nullable()
            .AddColumn("SceneEpisodeNumber").AsInt32().Nullable()
            .AddColumn("SceneAbsoluteEpisodeNumber").AsInt32().Nullable();

        Alter.Table("MediaFileDetails")
            .AddColumn("MediaInfo").AsString().Nullable()
            .AddColumn("MediaInfoUpdated").AsDateTime().Nullable();
    }

    public override void Down()
    {
        Delete.Table("AlternativeTitles");
    }
}
