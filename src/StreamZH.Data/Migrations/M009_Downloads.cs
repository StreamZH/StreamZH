// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Data;
using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// The built-in download queue. One row per download plus one row per file of its NZB, with a bitmap of the
/// segments already written (and one of the segments no server had), so a restart resumes without fetching them again.
/// </summary>
[Migration(9, "Download queue")]
public sealed class M009_Downloads : Migration
{
    public override void Up()
    {
        Create.Table("Downloads")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("Category").AsInt32().NotNullable()
            .WithColumn("Status").AsInt32().NotNullable().Indexed()
            .WithColumn("Priority").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("TotalBytes").AsInt64().NotNullable()
            .WithColumn("DownloadedBytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("MissingArticles").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Error").AsString().Nullable()
            .WithColumn("StatusMessage").AsString().Nullable()
            .WithColumn("Indexer").AsString().Nullable()
            .WithColumn("MediaType").AsInt32().Nullable()
            .WithColumn("MovieId").AsInt32().Nullable()
            .WithColumn("SeriesId").AsInt32().Nullable()
            .WithColumn("EpisodeIds").AsString().Nullable()
            .WithColumn("Password").AsString().Nullable()
            .WithColumn("NzbPath").AsString().NotNullable()
            .WithColumn("WorkFolder").AsString().NotNullable()
            .WithColumn("OutputPath").AsString().Nullable()
            .WithColumn("Added").AsDateTime().NotNullable()
            .WithColumn("Completed").AsDateTime().Nullable()
            .WithColumn("SortOrder").AsInt32().NotNullable().WithDefaultValue(0);

        // Done/Missing: one bit per segment in NZB order (bit i of byte i / 8). Wanted = false for par2 recovery
        // volumes that are held back until a repair needs them.
        Create.Table("DownloadFiles")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("DownloadId").AsInt32().NotNullable().ForeignKey("FK_DownloadFiles_Downloads", "Downloads", "Id").OnDelete(Rule.Cascade)
            .WithColumn("FileIndex").AsInt32().NotNullable()
            .WithColumn("FileName").AsString().NotNullable()
            .WithColumn("Kind").AsInt32().NotNullable()
            .WithColumn("TotalBytes").AsInt64().NotNullable()
            .WithColumn("Segments").AsInt32().NotNullable()
            .WithColumn("Wanted").AsBoolean().NotNullable()
            .WithColumn("DownloadedBytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("MissingArticles").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Done").AsBinary().NotNullable()
            .WithColumn("Missing").AsBinary().NotNullable();

        Create.Index("IX_DownloadFiles_DownloadId_FileIndex").OnTable("DownloadFiles")
            .OnColumn("DownloadId").Ascending()
            .OnColumn("FileIndex").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("DownloadFiles");
        Delete.Table("Downloads");
    }
}
