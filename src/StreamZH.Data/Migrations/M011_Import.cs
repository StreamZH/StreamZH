// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Import and rename (0.6): the import state of every finished download, the blocklist of releases that failed, and
/// what StreamZH knows about each media file it imported (quality, languages, release group), so renaming a file does
/// not lose its quality. No foreign keys, like the history: rows stay when an item leaves the library.
/// </summary>
[Migration(11, "Import, blocklist and media file details")]
public sealed class M011_Import : Migration
{
    public override void Up()
    {
        Create.Table("DownloadImports")
            .WithColumn("DownloadId").AsInt32().PrimaryKey()
            .WithColumn("State").AsInt32().NotNullable()
            .WithColumn("Message").AsString().Nullable()
            .WithColumn("Date").AsDateTime().NotNullable().Indexed()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("MovieId").AsInt32().Nullable()
            .WithColumn("SeriesId").AsInt32().Nullable()
            .WithColumn("EpisodeIds").AsString().Nullable()
            .WithColumn("Files").AsString().NotNullable()
            .WithColumn("IsUpgrade").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Manual").AsString().Nullable()
            .WithColumn("Dismissed").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Table("Blocklist")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Date").AsDateTime().NotNullable().Indexed()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("MovieId").AsInt32().Nullable().Indexed()
            .WithColumn("SeriesId").AsInt32().Nullable().Indexed()
            .WithColumn("EpisodeIds").AsString().Nullable()
            .WithColumn("SourceTitle").AsString().NotNullable()
            .WithColumn("QualityId").AsInt32().NotNullable()
            .WithColumn("Languages").AsString().NotNullable()
            .WithColumn("Indexer").AsString().Nullable()
            .WithColumn("Size").AsInt64().NotNullable()
            .WithColumn("Message").AsString().Nullable();

        Create.Table("MediaFileDetails")
            .WithColumn("Path").AsString().PrimaryKey()
            .WithColumn("QualityId").AsInt32().NotNullable()
            .WithColumn("RevisionVersion").AsInt32().NotNullable()
            .WithColumn("RevisionReal").AsInt32().NotNullable()
            .WithColumn("IsRepack").AsBoolean().NotNullable()
            .WithColumn("Languages").AsString().NotNullable()
            .WithColumn("ReleaseGroup").AsString().Nullable()
            .WithColumn("SceneName").AsString().Nullable()
            .WithColumn("Edition").AsString().Nullable()
            .WithColumn("VideoCodec").AsString().Nullable()
            .WithColumn("AudioCodec").AsString().Nullable()
            .WithColumn("DateAdded").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("MediaFileDetails");
        Delete.Table("Blocklist");
        Delete.Table("DownloadImports");
    }
}
