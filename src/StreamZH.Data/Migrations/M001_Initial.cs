// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

[Migration(1, "Initial schema: config and users")]
public sealed class M001_Initial : Migration
{
    public override void Up()
    {
        Create.Table("Config")
            .WithColumn("Key").AsString(200).PrimaryKey()
            .WithColumn("Value").AsString().NotNullable();

        Create.Table("Users")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Username").AsString(100).NotNullable().Unique()
            .WithColumn("PasswordHash").AsString().NotNullable()
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("CreatedAt").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("Users");
        Delete.Table("Config");
    }
}