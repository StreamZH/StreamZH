// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.10: Connect triggers for requests (all off for existing connections).</summary>
[Migration(25, "Request triggers for connections")]
public sealed class M025_RequestTriggers : Migration
{
    public override void Up()
    {
        Alter.Table("Connections")
            .AddColumn("OnRequestCreated").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("OnRequestApproved").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("OnRequestDeclined").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("OnRequestAvailable").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("OnRequestCreated").Column("OnRequestApproved").Column("OnRequestDeclined").Column("OnRequestAvailable").FromTable("Connections");
    }
}
