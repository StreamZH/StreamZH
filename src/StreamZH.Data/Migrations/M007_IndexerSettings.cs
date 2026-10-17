// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Data;
using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>Prowlarr-style indexer settings (enable, limits, VIP, extra parameters), stored capabilities and the indexer history.</summary>
[Migration(7, "Indexer settings, capabilities and indexer history")]
public sealed class M007_IndexerSettings : Migration
{
    public override void Up()
    {
        Alter.Table("Indexers")
            .AddColumn("Enable").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("DefinitionKey").AsString(100).Nullable()
            .AddColumn("AdditionalParameters").AsString().NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("VipExpiration").AsString(10).Nullable()
            .AddColumn("QueryLimit").AsInt32().Nullable()
            .AddColumn("GrabLimit").AsInt32().Nullable()
            .AddColumn("LimitsUnit").AsString(10).NotNullable().WithDefaultValue("day")
            .AddColumn("Capabilities").AsString().Nullable()
            .AddColumn("CapabilitiesCheckedAt").AsDateTime().Nullable();

        // Every API call StreamZH makes to an indexer (query, rss, grab, caps). Kept for 90 days.
        Create.Table("IndexerHistory")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Date").AsDateTime().NotNullable().Indexed()
            .WithColumn("IndexerId").AsInt32().NotNullable().ForeignKey("FK_IndexerHistory_Indexers", "Indexers", "Id").OnDelete(Rule.Cascade)
            .WithColumn("EventType").AsString(10).NotNullable()
            .WithColumn("Successful").AsBoolean().NotNullable()
            .WithColumn("ElapsedMs").AsInt32().NotNullable()
            .WithColumn("Source").AsString(20).NotNullable()
            .WithColumn("Query").AsString().Nullable()
            .WithColumn("Results").AsInt32().Nullable()
            .WithColumn("Title").AsString().Nullable()
            .WithColumn("Error").AsString().Nullable();

        Create.Index("IX_IndexerHistory_IndexerId_Date").OnTable("IndexerHistory")
            .OnColumn("IndexerId").Ascending()
            .OnColumn("Date").Ascending();
    }

    public override void Down()
    {
        Delete.Table("IndexerHistory");
        Delete.Column("Enable").Column("DefinitionKey").Column("AdditionalParameters").Column("VipExpiration").Column("QueryLimit")
            .Column("GrabLimit").Column("LimitsUnit").Column("Capabilities").Column("CapabilitiesCheckedAt").FromTable("Indexers");
    }
}
