// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using FluentMigrator;

namespace StreamZH.Data.Migrations;

/// <summary>0.10: user management (profile, limits, sessions), requests, invites and Discord account links.</summary>
[Migration(24, "Users, requests, invites, Discord links")]
public sealed class M024_UsersRequests : Migration
{
    public override void Up()
    {
        Alter.Table("Users")
            .AddColumn("DisplayName").AsString(100).Nullable()
            .AddColumn("Email").AsString(200).Nullable()
            .AddColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("LastLogin").AsDateTime().Nullable()
            // Bumped on password change, role change, disable: cookies issued before stop working.
            .AddColumn("SessionVersion").AsInt32().NotNullable().WithDefaultValue(0)
            // Null = the role's default (Owner/Admin/Friend approve themselves, Requester waits).
            .AddColumn("AutoApprove").AsBoolean().Nullable()
            // Request limits: at most N movies / N series(seasons) per LimitDays; null = no limit.
            .AddColumn("MovieRequestLimit").AsInt32().Nullable()
            .AddColumn("SeriesRequestLimit").AsInt32().Nullable()
            .AddColumn("RequestLimitDays").AsInt32().Nullable();

        Create.Table("Requests")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("MediaType").AsInt32().NotNullable()
            .WithColumn("TmdbId").AsInt32().NotNullable()
            .WithColumn("Title").AsString(500).NotNullable()
            .WithColumn("Year").AsInt32().Nullable()
            .WithColumn("PosterPath").AsString(300).Nullable()
            // JSON array of season numbers for series; null = all seasons / a movie.
            .WithColumn("Seasons").AsString().Nullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("Source").AsInt32().NotNullable()
            .WithColumn("RequestedBy").AsInt32().Nullable()
            .WithColumn("DiscordUserId").AsString(32).Nullable()
            .WithColumn("DiscordName").AsString(100).Nullable()
            .WithColumn("QualityProfileId").AsInt32().Nullable()
            .WithColumn("RootFolderId").AsInt32().Nullable()
            .WithColumn("ItemId").AsInt32().Nullable()
            .WithColumn("Note").AsString(1000).Nullable()
            .WithColumn("Reason").AsString(1000).Nullable()
            .WithColumn("DecidedBy").AsInt32().Nullable()
            .WithColumn("Created").AsDateTime().NotNullable()
            .WithColumn("Updated").AsDateTime().NotNullable()
            .WithColumn("Decided").AsDateTime().Nullable()
            .WithColumn("Available").AsDateTime().Nullable();
        Create.Index("IX_Requests_Item").OnTable("Requests").OnColumn("MediaType").Ascending().OnColumn("TmdbId").Ascending();
        Create.Index("IX_Requests_User").OnTable("Requests").OnColumn("RequestedBy").Ascending();
        Create.Index("IX_Requests_Status").OnTable("Requests").OnColumn("Status").Ascending();

        Create.Table("Invites")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            // SHA-256 of the token; the token itself is shown once when the invite is created.
            .WithColumn("TokenHash").AsString(64).NotNullable().Unique()
            .WithColumn("Role").AsInt32().NotNullable()
            .WithColumn("Note").AsString(200).Nullable()
            .WithColumn("CreatedBy").AsInt32().NotNullable()
            .WithColumn("Created").AsDateTime().NotNullable()
            .WithColumn("Expires").AsDateTime().Nullable()
            .WithColumn("MaxUses").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("Uses").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Revoked").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Table("DiscordLinks")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("UserId").AsInt32().NotNullable().Unique()
            .WithColumn("DiscordUserId").AsString(32).NotNullable().Unique()
            .WithColumn("DiscordName").AsString(100).Nullable()
            .WithColumn("Linked").AsDateTime().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("DiscordLinks");
        Delete.Table("Invites");
        Delete.Table("Requests");
    }
}
