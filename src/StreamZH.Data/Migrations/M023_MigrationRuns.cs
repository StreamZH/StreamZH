// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.9: runs of the migration from Radarr, Sonarr, Prowlarr and SABnzbd (report as JSON; API keys are never stored).</summary>
[Migration(23, "Migration runs")]
public sealed class M023_MigrationRuns : Migration
{
    public override void Up()
    {
        Create.Table("MigrationRuns")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Source").AsString(20).NotNullable()
            .WithColumn("Status").AsString(20).NotNullable()
            .WithColumn("DryRun").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Started").AsDateTime().NotNullable()
            .WithColumn("Finished").AsDateTime().Nullable()
            .WithColumn("Error").AsString().Nullable()
            .WithColumn("App").AsString().Nullable()
            .WithColumn("Report").AsString().Nullable()
            .WithColumn("UserName").AsString(100).Nullable();
    }

    public override void Down()
    {
        Delete.Table("MigrationRuns");
    }
}
