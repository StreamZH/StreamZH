// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>Usenet provider accounts for the built-in downloader. The password is stored as is, like indexer API keys.</summary>
[Migration(8, "Usenet servers")]
public sealed class M008_UsenetServers : Migration
{
    public override void Up()
    {
        Create.Table("UsenetServers")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Host").AsString(255).NotNullable()
            .WithColumn("Port").AsInt32().NotNullable()
            .WithColumn("UseSsl").AsBoolean().NotNullable()
            .WithColumn("Username").AsString().NotNullable()
            .WithColumn("Password").AsString().NotNullable()
            .WithColumn("Connections").AsInt32().NotNullable()
            .WithColumn("Priority").AsInt32().NotNullable()
            .WithColumn("Enabled").AsBoolean().NotNullable()
            .WithColumn("RetentionDays").AsInt32().NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable();
    }

    public override void Down() => Delete.Table("UsenetServers");
}
