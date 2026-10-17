// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>
/// Settings → Connect (like Radarr's "Connect"): media servers (Plex, Jellyfin, Emby, Kodi) and notification services,
/// each with its triggers, its type-specific settings (JSON) and the result of the last delivery.
/// </summary>
[Migration(15, "Connections (media servers and notifications)")]
public sealed class M015_Connections : Migration
{
    public override void Up()
    {
        Create.Table("Connections")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable().Unique()
            .WithColumn("Implementation").AsString(50).NotNullable()
            .WithColumn("Enable").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("OnGrab").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnImport").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnUpgrade").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnRename").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnDownloadFailure").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnHealthIssue").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("OnHealthRestored").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("IncludeHealthWarnings").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("Settings").AsString().NotNullable().WithDefaultValue("{}")
            .WithColumn("Added").AsDateTime().NotNullable()
            .WithColumn("LastSuccess").AsDateTime().Nullable()
            .WithColumn("LastFailure").AsDateTime().Nullable()
            .WithColumn("LastError").AsString().Nullable();
    }

    public override void Down() => Delete.Table("Connections");
}
