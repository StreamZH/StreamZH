// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.12: sign-in sessions, so signing out ends a cookie on the server too (security review S1-10).</summary>
[Migration(27, "Sessions")]
public sealed class M027_Sessions : Migration
{
    public override void Up()
    {
        Create.Table("Sessions")
            .WithColumn("Id").AsString(64).PrimaryKey()
            .WithColumn("UserId").AsInt32().NotNullable().Indexed()
            .WithColumn("Created").AsDateTime().NotNullable()
            .WithColumn("LastSeen").AsDateTime().NotNullable()
            .WithColumn("Persistent").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down() => Delete.Table("Sessions");
}
