// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using FluentMigrator;

namespace StreamZH.Data.Migrations;

[Migration(3, "Quality profiles and quality definitions")]
public sealed class M003_Profiles : Migration
{
    public override void Up()
    {
        Create.Table("QualityProfiles")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Items").AsString().NotNullable()
            .WithColumn("CutoffQualityId").AsInt32().NotNullable()
            .WithColumn("UpgradeAllowed").AsBoolean().NotNullable()
            .WithColumn("LanguageId").AsInt32().NotNullable()
            .WithColumn("PreferDualLanguage").AsBoolean().NotNullable();

        // Size limits in MB per minute of runtime; null means no limit.
        Create.Table("QualityDefinitions")
            .WithColumn("QualityId").AsInt32().PrimaryKey()
            .WithColumn("Title").AsString(100).NotNullable()
            .WithColumn("MinSize").AsDouble().NotNullable()
            .WithColumn("MaxSize").AsDouble().Nullable()
            .WithColumn("PreferredSize").AsDouble().Nullable();

        foreach (var profile in QualityDefaults.Profiles)
        {
            Insert.IntoTable("QualityProfiles").Row(new
            {
                profile.Id,
                profile.Name,
                Items = QualityProfileItemsJson.Serialize(profile.Items),
                profile.CutoffQualityId,
                profile.UpgradeAllowed,
                profile.LanguageId,
                profile.PreferDualLanguage,
            });
        }

        foreach (var definition in QualityDefaults.Definitions)
        {
            Insert.IntoTable("QualityDefinitions").Row(new
            {
                definition.QualityId,
                definition.Title,
                definition.MinSize,
                definition.MaxSize,
                definition.PreferredSize,
            });
        }

        Insert.IntoTable("Config").Row(new
        {
            Key = ConfigKeys.DefaultQualityProfileId,
            Value = QualityDefaults.DefaultProfileId.ToString(CultureInfo.InvariantCulture),
        });

        // Existing movies and series get the default profile.
        Alter.Table("Movies").AddColumn("QualityProfileId").AsInt32().NotNullable().WithDefaultValue(QualityDefaults.DefaultProfileId);
        Alter.Table("Series").AddColumn("QualityProfileId").AsInt32().NotNullable().WithDefaultValue(QualityDefaults.DefaultProfileId);
    }

    public override void Down()
    {
        Delete.Column("QualityProfileId").FromTable("Series");
        Delete.Column("QualityProfileId").FromTable("Movies");
        Delete.FromTable("Config").Row(new { Key = ConfigKeys.DefaultQualityProfileId });
        Delete.Table("QualityDefinitions");
        Delete.Table("QualityProfiles");
    }
}
