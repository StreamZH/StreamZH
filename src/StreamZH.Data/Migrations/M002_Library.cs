// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

[Migration(2, "Library: root folders, movies, series, episodes")]
public sealed class M002_Library : Migration
{
    public override void Up()
    {
        Create.Table("RootFolders")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Path").AsString().NotNullable().Unique()
            .WithColumn("MediaType").AsInt32().NotNullable();

        Create.Table("Movies")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TmdbId").AsInt32().NotNullable().Unique()
            .WithColumn("ImdbId").AsString().Nullable()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("OriginalTitle").AsString().NotNullable()
            .WithColumn("SortTitle").AsString().NotNullable()
            .WithColumn("Year").AsInt32().NotNullable()
            .WithColumn("Overview").AsString().Nullable()
            .WithColumn("Runtime").AsInt32().NotNullable()
            .WithColumn("Status").AsString().NotNullable()
            .WithColumn("Genres").AsString().NotNullable()
            .WithColumn("PosterPath").AsString().Nullable()
            .WithColumn("BackdropPath").AsString().Nullable()
            .WithColumn("Monitored").AsBoolean().NotNullable()
            .WithColumn("RootFolderId").AsInt32().NotNullable().ForeignKey("RootFolders", "Id")
            .WithColumn("FolderPath").AsString().NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable()
            .WithColumn("LastRefreshed").AsDateTime().Nullable()
            .WithColumn("FileRelativePath").AsString().Nullable()
            .WithColumn("FileSize").AsInt64().Nullable()
            .WithColumn("FileModified").AsDateTime().Nullable();

        Create.Table("Series")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("TmdbId").AsInt32().NotNullable().Unique()
            .WithColumn("TvdbId").AsInt32().Nullable()
            .WithColumn("ImdbId").AsString().Nullable()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("OriginalTitle").AsString().NotNullable()
            .WithColumn("SortTitle").AsString().NotNullable()
            .WithColumn("Year").AsInt32().NotNullable()
            .WithColumn("Overview").AsString().Nullable()
            .WithColumn("Status").AsString().NotNullable()
            .WithColumn("Network").AsString().Nullable()
            .WithColumn("Genres").AsString().NotNullable()
            .WithColumn("PosterPath").AsString().Nullable()
            .WithColumn("BackdropPath").AsString().Nullable()
            .WithColumn("Monitored").AsBoolean().NotNullable()
            .WithColumn("RootFolderId").AsInt32().NotNullable().ForeignKey("RootFolders", "Id")
            .WithColumn("FolderPath").AsString().NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable()
            .WithColumn("LastRefreshed").AsDateTime().Nullable();

        Create.Table("Episodes")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("SeriesId").AsInt32().NotNullable().ForeignKey("Series", "Id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("SeasonNumber").AsInt32().NotNullable()
            .WithColumn("EpisodeNumber").AsInt32().NotNullable()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("AirDate").AsString().Nullable()
            .WithColumn("Overview").AsString().Nullable()
            .WithColumn("Monitored").AsBoolean().NotNullable()
            .WithColumn("FileRelativePath").AsString().Nullable()
            .WithColumn("FileSize").AsInt64().Nullable()
            .WithColumn("FileModified").AsDateTime().Nullable();

        Create.Index("IX_Episodes_Series_Season_Episode").OnTable("Episodes")
            .OnColumn("SeriesId").Ascending()
            .OnColumn("SeasonNumber").Ascending()
            .OnColumn("EpisodeNumber").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("Episodes");
        Delete.Table("Series");
        Delete.Table("Movies");
        Delete.Table("RootFolders");
    }
}