// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.Auth;
using StreamZH.Host.General;
using StreamZH.Library.Discord;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → Discord (0.10): the built-in bot that replaces Requestrr. The bot token is write-only: it comes back as
/// "********" (with <c>hasToken</c>), which keeps the saved token when sent again; "" removes it. Admin only.
/// </summary>
public static class DiscordEndpoints
{
    /// <summary>The bot and its settings (one line in Program.cs). <c>--discord-url</c> points the REST client at a stand-in (development only).</summary>
    public static IServiceCollection AddDiscord(this IServiceCollection services, IConfiguration configuration)
    {
        // The gateway websocket uses the proxy of Settings → General (registered before AddDiscordBot, which only adds defaults).
        services.AddSingleton(sp => new DiscordGatewayOptions
        {
            Connect = DiscordGatewayOptions.DefaultConnect(() => sp.GetRequiredService<SettingsWebProxy>()),
        });
        var apiUrl = configuration["discord-url"] is { Length: > 0 } url ? new Uri(url.EndsWith('/') ? url : url + "/") : null;
        services.AddDiscordBot(apiUrl);
        return services;
    }

    public static IEndpointRouteBuilder MapDiscordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings/discord").WithTags("Settings").RequireAuthorization(AuthSchemes.AdminPolicy);

        group.MapGet("/", (DiscordBotSettings settings, DiscordBotStatusHolder status, IRequestGateway requests) =>
            DiscordSettingsResource.From(settings.Values, status.Current, requests));

        group.MapPut("/", (
            DiscordSettingsChange change,
            DiscordBotSettings settings,
            DiscordBotStatusHolder status,
            IRequestGateway requests,
            ILogger<DiscordSettingsResource> logger) =>
        {
            var errors = settings.Validate(change);
            var tokenAfter = change.BotToken switch
            {
                null or DiscordBotSettings.Mask => settings.Token is not null,
                { } given => given.Trim().Length > 0,
            };
            if ((change.Enabled ?? settings.Values.Enabled) && !tokenAfter && !errors.ContainsKey("botToken"))
            {
                errors["botToken"] = ["Enter the bot token to switch the bot on."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var tokenChanged = change.BotToken is { } token && token != DiscordBotSettings.Mask;
            settings.Update(change);
            var values = settings.Values;
            logger.LogInformation(
                "Discord settings saved: bot {Enabled}, token {Token}, {Guilds} servers, {Channels} channels, guests {Guests}",
                values.Enabled ? "on" : "off",
                tokenChanged ? values.HasToken ? "replaced" : "removed" : values.HasToken ? "kept" : "none",
                values.GuildIds.Count,
                values.ChannelIds.Count,
                values.AllowGuests ? "allowed" : "not allowed");
            return Results.Ok(DiscordSettingsResource.From(values, status.Current, requests));
        });

        group.MapGet("/status", (DiscordBotStatusHolder status) => DiscordStatusResource.From(status.Current));

        // Nothing is saved. Without a token (or with the mask) the saved token is tested.
        group.MapPost("/test", async (DiscordTestRequest? request, DiscordBotSettings settings, DiscordBotTester tester, CancellationToken ct) =>
        {
            var given = request?.BotToken?.Trim();
            if (given is { Length: > 0 } && given != DiscordBotSettings.Mask && given.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase))
            {
                given = given[4..].Trim();
            }

            var token = given is { Length: > 0 } && given != DiscordBotSettings.Mask ? given : settings.Token;
            if (token is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["botToken"] = ["Enter the bot token to test it."] });
            }

            if (!DiscordBotSettings.LooksLikeToken(token))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["botToken"] = ["This does not look like a bot token. Copy it from Bot → Reset Token in the Discord Developer Portal."],
                });
            }

            return Results.Ok(await tester.TestAsync(token, ct));
        });

        // Preview by default; with apply: true the shown changes are saved.
        group.MapPost("/import-requestrr", (RequestrrImportRequest request, DiscordBotSettings settings, DiscordBotStatusHolder status, IRequestGateway requests, ILogger<DiscordSettingsResource> logger) =>
        {
            RequestrrImportPreview preview;
            try
            {
                preview = RequestrrImport.Preview(request.Settings ?? string.Empty, settings.Values, request.TakeToken);
            }
            catch (RequestrrImportException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [ex.Message] });
            }

            if (!request.Apply)
            {
                return Results.Ok(RequestrrImportResource.From(preview, applied: false, null));
            }

            var errors = settings.Validate(preview.Change);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [.. errors.SelectMany(e => e.Value)] });
            }

            settings.Update(preview.Change);
            logger.LogInformation(
                "Discord settings imported from Requestrr ({Changes} changes, token {Token})",
                preview.Rows.Count,
                preview.Change.BotToken is null ? "not taken over" : "taken over");
            return Results.Ok(RequestrrImportResource.From(preview, applied: true, DiscordSettingsResource.From(settings.Values, status.Current, requests)));
        });

        return app;
    }
}

public sealed record DiscordTestRequest(string? BotToken);

/// <param name="Settings">The content of Requestrr's config/settings.json.</param>
/// <param name="TakeToken">Also take over Requestrr's bot token.</param>
/// <param name="Apply">false = preview only.</param>
public sealed record RequestrrImportRequest(string? Settings, bool TakeToken = false, bool Apply = false);

public sealed record RequestrrImportResource(
    IReadOnlyList<RequestrrImportRow> Rows, IReadOnlyList<string> Warnings, bool HasToken, bool TakesToken, string? Version, bool Applied, DiscordSettingsResource? Settings)
{
    public static RequestrrImportResource From(RequestrrImportPreview preview, bool applied, DiscordSettingsResource? settings) =>
        new(preview.Rows, preview.Warnings, preview.HasToken, preview.Change.BotToken is not null, preview.Version, applied, settings);
}

/// <summary>The bot's state for Settings → Discord.</summary>
/// <param name="State">off, connecting, connected, reconnecting or error.</param>
public sealed record DiscordStatusResource(
    string State, string? BotName, string? BotId, int Guilds, string? Error, DateTimeOffset? Since, DateTimeOffset? NextRetry, string? PermissionProblem)
{
    public static DiscordStatusResource From(DiscordBotStatus status) =>
        new(status.State, status.BotName, status.BotId, status.Guilds, status.Error, status.Since, status.NextRetry, status.PermissionProblem);
}

/// <summary>Settings → Discord as the API returns it; <see cref="BotToken"/> is "********" when a token is saved, else "".</summary>
/// <param name="InviteUrl">Adds the bot to a server with the scopes and permissions it needs; null without an application id.</param>
/// <param name="RequestsReady">The requests module answers the bot (false only in builds without it).</param>
public sealed record DiscordSettingsResource(
    bool Enabled,
    bool HasToken,
    string BotToken,
    string? ApplicationId,
    string? InviteUrl,
    IReadOnlyList<string> GuildIds,
    IReadOnlyList<string> ChannelIds,
    bool AllowGuests,
    IReadOnlyList<string> GuestRoleIds,
    string? NotifyChannelId,
    bool DmRequesters,
    bool AllowDirectMessages,
    string MovieCommand,
    string TvCommand,
    string SeriesSeasons,
    string StatusText,
    DiscordStatusResource Status,
    bool RequestsReady)
{
    public static DiscordSettingsResource From(DiscordSettingsValues values, DiscordBotStatus status, IRequestGateway requests)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(status);
        var applicationId = values.ApplicationId ?? status.ApplicationId;
        return new DiscordSettingsResource(
            values.Enabled,
            values.HasToken,
            values.HasToken ? DiscordBotSettings.Mask : string.Empty,
            applicationId,
            DiscordBotSettings.InviteUrl(applicationId),
            values.GuildIds,
            values.ChannelIds,
            values.AllowGuests,
            values.GuestRoleIds,
            values.NotifyChannelId,
            values.DmRequesters,
            values.AllowDirectMessages,
            values.MovieCommand,
            values.TvCommand,
            values.SeriesSeasons,
            values.StatusText,
            DiscordStatusResource.From(status),
            requests is not RequestsNotReadyGateway);
    }
}
