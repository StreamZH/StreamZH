// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>1.0.0-rc.7: brake per indexer (requests per second); empty = the default of 2.</summary>
[Migration(30, "Indexer requests per second")]
public sealed class M030_IndexerRequestsPerSecond : Migration
{
    public override void Up() => Alter.Table("Indexers").AddColumn("RequestsPerSecond").AsDouble().Nullable();

    public override void Down() => Delete.Column("RequestsPerSecond").FromTable("Indexers");
}
