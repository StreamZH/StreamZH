// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Data;
using FluentMigrator;

namespace StreamZH.Data.Migrations;

[Migration(5, "Indexers and indexer health")]
public sealed class M005_Indexers : Migration
{
    public override void Up()
    {
        // Categories are stored as comma-separated ids ("2000,2040").
        Create.Table("Indexers")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Implementation").AsString(50).NotNullable()
            .WithColumn("BaseUrl").AsString().NotNullable()
            .WithColumn("ApiPath").AsString().NotNullable()
            .WithColumn("ApiKey").AsString().NotNullable()
            .WithColumn("Categories").AsString().NotNullable()
            .WithColumn("TvCategories").AsString().NotNullable()
            .WithColumn("Priority").AsInt32().NotNullable()
            .WithColumn("EnableRss").AsBoolean().NotNullable()
            .WithColumn("EnableAutomaticSearch").AsBoolean().NotNullable()
            .WithColumn("EnableInteractiveSearch").AsBoolean().NotNullable()
            .WithColumn("Added").AsDateTime().NotNullable();

        // One row per indexer that failed recently; removed again on the next success.
        Create.Table("IndexerStatus")
            .WithColumn("IndexerId").AsInt32().PrimaryKey().ForeignKey("FK_IndexerStatus_Indexers", "Indexers", "Id").OnDelete(Rule.Cascade)
            .WithColumn("InitialFailure").AsDateTime().Nullable()
            .WithColumn("MostRecentFailure").AsDateTime().Nullable()
            .WithColumn("EscalationLevel").AsInt32().NotNullable()
            .WithColumn("DisabledTill").AsDateTime().Nullable()
            .WithColumn("LastError").AsString().Nullable();
    }

    public override void Down()
    {
        Delete.Table("IndexerStatus");
        Delete.Table("Indexers");
    }
}
