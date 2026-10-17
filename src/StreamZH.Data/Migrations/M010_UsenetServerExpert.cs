// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Expert settings of Usenet servers (like SABnzbd's server page): timeout, certificate check, articles per request,
/// what happens on failure, account expiry, download quota with its counter, and notes. Defaults keep the old behaviour.
/// </summary>
[Migration(10, "Usenet server expert settings")]
public sealed class M010_UsenetServerExpert : Migration
{
    public override void Up()
    {
        Alter.Table("UsenetServers")
            .AddColumn("TimeoutSeconds").AsInt32().NotNullable().WithDefaultValue(60)
            .AddColumn("CertificateCheck").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("ArticlesPerRequest").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("OnFailure").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("ExpiresOn").AsString(10).Nullable()
            .AddColumn("QuotaBytes").AsInt64().Nullable()
            .AddColumn("QuotaUsedBytes").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("QuotaSince").AsDateTime().Nullable()
            .AddColumn("Notes").AsString().NotNullable().WithDefaultValue(string.Empty);
    }

    public override void Down() =>
        Delete.Column("TimeoutSeconds").Column("CertificateCheck").Column("ArticlesPerRequest").Column("OnFailure").Column("ExpiresOn")
            .Column("QuotaBytes").Column("QuotaUsedBytes").Column("QuotaSince").Column("Notes").FromTable("UsenetServers");
}
