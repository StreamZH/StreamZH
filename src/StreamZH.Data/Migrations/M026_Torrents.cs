// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.11: torrents — seed settings on indexers, protocol and client fields on downloads, download clients, remote path mappings.</summary>
[Migration(26, "Torrents")]
public sealed class M026_Torrents : Migration
{
    public override void Up()
    {
        Alter.Table("Indexers")
            .AddColumn("MinimumSeeders").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("SeedRatio").AsDouble().Nullable()
            .AddColumn("SeedTimeMinutes").AsInt32().Nullable()
            .AddColumn("SeasonPackSeedTimeMinutes").AsInt32().Nullable();

        Alter.Table("Downloads")
            .AddColumn("Protocol").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("ClientId").AsInt32().Nullable()
            .AddColumn("ExternalId").AsString(64).Nullable().Indexed()
            .AddColumn("Seeding").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("Ratio").AsDouble().Nullable()
            .AddColumn("UploadedBytes").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("Seeders").AsInt32().Nullable()
            .AddColumn("Peers").AsInt32().Nullable()
            .AddColumn("SeedRatio").AsDouble().Nullable()
            .AddColumn("SeedTimeMinutes").AsInt32().Nullable();

        Create.Table("DownloadClients")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Implementation").AsString(50).NotNullable()
            .WithColumn("Protocol").AsInt32().NotNullable()
            .WithColumn("Enable").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Priority").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("RemoveCompleted").AsBoolean().NotNullable().WithDefaultValue(true)
            // JSON object with the implementation's fields (stored like other secrets, see 0.12 security review).
            .WithColumn("Settings").AsString().NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable();

        Create.Table("RemotePathMappings")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Host").AsString(255).NotNullable()
            .WithColumn("RemotePath").AsString(1000).NotNullable()
            .WithColumn("LocalPath").AsString(1000).NotNullable();
    }

    public override void Down()
    {
        Delete.Table("RemotePathMappings");
        Delete.Table("DownloadClients");
    }
}
