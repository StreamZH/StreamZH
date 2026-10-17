// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Settings → Import lists (Radarr's "Lists", Sonarr's "Import Lists"): the lists with their add options, type-specific
/// settings (JSON) and the result of the last sync; the exclusions; and which library item a list added (for the
/// "add to exclusions" question on remove and for "Clean library").
/// </summary>
[Migration(19, "Import lists, exclusions and list origins")]
public sealed class M019_ImportLists : Migration
{
    public override void Up()
    {
        Create.Table("ImportLists")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Implementation").AsString(50).NotNullable()
            .WithColumn("MediaType").AsString(10).NotNullable()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("EnableAuto").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("SearchOnAdd").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Monitor").AsString(30).NotNullable()
            .WithColumn("RootFolderId").AsInt32().NotNullable()
            .WithColumn("QualityProfileId").AsInt32().Nullable()
            .WithColumn("MinimumAvailability").AsString(20).NotNullable().WithDefaultValue("announced")
            .WithColumn("Settings").AsString().NotNullable().WithDefaultValue("{}")
            .WithColumn("Added").AsDateTime().NotNullable()
            .WithColumn("LastSync").AsDateTime().Nullable()
            .WithColumn("LastSuccess").AsDateTime().Nullable()
            .WithColumn("LastError").AsString().Nullable()
            .WithColumn("Failures").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("LastFound").AsInt32().Nullable()
            .WithColumn("LastAdded").AsInt32().Nullable()
            .WithColumn("LastAddedTitles").AsString().NotNullable().WithDefaultValue("[]");

        Create.Table("ImportListExclusions")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("MediaType").AsString(10).NotNullable()
            .WithColumn("TmdbId").AsInt32().NotNullable()
            .WithColumn("Title").AsString().NotNullable()
            .WithColumn("Year").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Added").AsDateTime().NotNullable();
        Create.Index("IX_ImportListExclusions_Media").OnTable("ImportListExclusions")
            .OnColumn("MediaType").Ascending().OnColumn("TmdbId").Ascending().WithOptions().Unique();

        Create.Table("ImportListOrigins")
            .WithColumn("MediaType").AsString(10).NotNullable().PrimaryKey()
            .WithColumn("TmdbId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("ListId").AsInt32().NotNullable()
            .WithColumn("ListName").AsString(100).NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("ImportListOrigins");
        Delete.Table("ImportListExclusions");
        Delete.Table("ImportLists");
    }
}
