// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Settings → Downloads like SABnzbd: download categories (folder, priority, post-processing; "movies", "series" and
/// "other" are the defaults and match the folders used so far), the download scheduler, and per download its category
/// name, a fingerprint of its NZB for duplicate detection and the duplicate tag.
/// </summary>
[Migration(12, "Download categories, scheduler and duplicate detection")]
public sealed class M012_DownloadSettings : Migration
{
    public override void Up()
    {
        // Folder: "" = the name, relative = below the completed folder, absolute = anywhere.
        // Priority: null = default (the request's), -2 = paused, -1 low … 2 force. PostProcessing: 0 none … 3 full (+Delete).
        Create.Table("DownloadCategories")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(64).NotNullable().Unique()
            .WithColumn("Folder").AsString().NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("Priority").AsInt32().Nullable()
            .WithColumn("PostProcessing").AsInt32().NotNullable().WithDefaultValue(3);

        foreach (var name in new[] { "movies", "series", "other" })
        {
            Insert.IntoTable("DownloadCategories").Row(new { Name = name, Folder = string.Empty, PostProcessing = 3 });
        }

        // Days: bit 0 = Monday … bit 6 = Sunday. Time: minutes after local midnight.
        Create.Table("DownloadSchedules")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Minute").AsInt32().NotNullable()
            .WithColumn("Days").AsInt32().NotNullable()
            .WithColumn("Action").AsString(32).NotNullable()
            .WithColumn("Argument").AsString().NotNullable().WithDefaultValue(string.Empty);

        Alter.Table("Downloads")
            .AddColumn("CategoryName").AsString(64).Nullable()
            .AddColumn("NzbHash").AsString(64).Nullable().Indexed()
            .AddColumn("Duplicate").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Index().OnTable("Downloads").OnColumn("NzbHash");
        Delete.Column("CategoryName").Column("NzbHash").Column("Duplicate").FromTable("Downloads");
        Delete.Table("DownloadSchedules");
        Delete.Table("DownloadCategories");
    }
}
