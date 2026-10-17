// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

[Migration(4, "Original language of movies and series (from TMDB)")]
public sealed class M004_OriginalLanguage : Migration
{
    public override void Up()
    {
        Alter.Table("Movies").AddColumn("OriginalLanguageId").AsInt32().NotNullable().WithDefaultValue(0);
        Alter.Table("Series").AddColumn("OriginalLanguageId").AsInt32().NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        Delete.Column("OriginalLanguageId").FromTable("Movies");
        Delete.Column("OriginalLanguageId").FromTable("Series");
    }
}
