// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Library;
using StreamZH.Library.Search;
using StreamZH.Library.Usenet;
using StreamZH.Usenet.Pool;

namespace StreamZH.Host.Api;

/// <summary>Usenet provider accounts (add/edit/delete/test) and the live state of their connections.</summary>
public static class UsenetEndpoints
{
    /// <summary>
    /// Repository, <see cref="UsenetServerService"/>, and <see cref="UsenetArticleSource"/> (also as <see cref="IArticleSource"/>,
    /// same instance) loaded with the stored servers. <see cref="UsenetPoolOptions"/> can be replaced before this runs (tests).
    /// </summary>
    public static IServiceCollection AddUsenet(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // "Tries per article" is a download setting (Settings → Downloads, advanced).
        services.TryAddSingleton(sp => new UsenetPoolOptions { AttemptsSetting = () => sp.GetRequiredService<DownloadSettings>().ArticleTries });
        services.AddSingleton<UsenetServerRepository>();
        services.AddSingleton(sp =>
        {
            var repository = sp.GetRequiredService<UsenetServerRepository>();
            var source = new UsenetArticleSource(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<UsenetArticleSource>>(),
                sp.GetRequiredService<UsenetPoolOptions>())
            {
                SaveQuotaUsage = repository.AddQuotaUsage,
            };
            source.Configure(repository.All());

            // Server down or back, quota used up or reset: the status window shows it.
            if (sp.GetService<IEventBus>() is { } events)
            {
                source.StateChanged += () => _ = PublishOngoingAsync(events);
            }

            return source;
        });
        services.AddSingleton<IArticleSource>(sp => sp.GetRequiredService<UsenetArticleSource>());
        services.AddSingleton<UsenetServerService>();
        return services;
    }

    public static IEndpointRouteBuilder MapUsenetEndpoints(this IEndpointRouteBuilder app)
    {
        var usenet = app.MapGroup("/api/v1/usenet").RequireAuthorization().WithTags("Usenet");

        usenet.MapGet("/server", (UsenetServerService service) => service.All().Select(UsenetServerResource.From));
        usenet.MapGet("/server/{id:int}", (int id, UsenetServerService service) =>
            service.Find(id) is { } server ? Results.Ok(UsenetServerResource.From(server)) : Results.NotFound());

        usenet.MapPost("/server", (UsenetServerRequest request, UsenetServerService service) =>
        {
            var created = service.Create(request.ToServer());
            return Results.Created($"/api/v1/usenet/server/{created.Id}", UsenetServerResource.From(created));
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapPut("/server/{id:int}", (int id, UsenetServerRequest request, UsenetServerService service) =>
            UsenetServerResource.From(service.Update(id, request.ToServer())))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapDelete("/server/{id:int}", (int id, UsenetServerService service) =>
        {
            service.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // Nothing is saved. "********" as password with the id of a stored server uses its stored password.
        usenet.MapPost("/server/test", async (UsenetServerRequest request, UsenetServerService service, CancellationToken cancellationToken) =>
        {
            var result = await service.TestAsync(request.ToServer(), cancellationToken);
            return result.Ok
                ? Results.Ok(new UsenetTestResource(true, result.Message, result.LatencyMs))
                : Results.Problem(
                    title: result.Message,
                    detail: result.Message,
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "server-test-failed" });
        }).RequireAuthorization(AuthSchemes.AdminPolicy);

        // The quota counter starts again from zero; a server whose quota was used up is used again.
        usenet.MapPost("/server/{id:int}/quota/reset", (int id, UsenetServerService service) =>
            UsenetServerResource.From(service.ResetQuota(id)))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        usenet.MapGet("/status", (UsenetServerService service) => service.Status());

        return app;
    }

    private static async Task PublishOngoingAsync(IEventBus events)
    {
        try
        {
            await events.PublishAsync(new OngoingChanged());
        }
        catch (Exception)
        {
            // Only a hint for the browsers to re-read the status window.
        }
    }
}

/// <summary>A server as the API returns it. The password is never sent: "********" when one is stored, else empty.</summary>
/// <param name="CertificateCheck">"strict", "minimal" or "off".</param>
/// <param name="OnFailure">"skip", "pause" (required server) or "ignore" (optional server).</param>
/// <param name="ExpiresOn">"yyyy-MM-dd" or null.</param>
/// <param name="QuotaGb">Quota in GB (10^9 bytes, as providers count); null = none.</param>
/// <param name="QuotaUsedBytes">Downloaded since <paramref name="QuotaSince"/>; 0 without a quota.</param>
public sealed record UsenetServerResource(
    int Id,
    string Name,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Password,
    int Connections,
    int Priority,
    bool Enabled,
    int RetentionDays,
    int TimeoutSeconds,
    string CertificateCheck,
    int ArticlesPerRequest,
    string OnFailure,
    string? ExpiresOn,
    double? QuotaGb,
    long QuotaUsedBytes,
    DateTimeOffset? QuotaSince,
    string Notes)
{
    public static UsenetServerResource From(UsenetServer s) => new(
        s.Id,
        s.Name,
        s.Host,
        s.Port,
        s.UseSsl,
        s.Username,
        string.IsNullOrEmpty(s.Password) ? string.Empty : UsenetServerService.MaskedPassword,
        s.Connections,
        s.Priority,
        s.Enabled,
        s.RetentionDays,
        s.TimeoutSeconds,
        UsenetServerValues.Name(s.CertificateCheck),
        s.ArticlesPerRequest,
        UsenetServerValues.Name(s.OnFailure),
        s.ExpiresOn,
        s.QuotaBytes is { } quota ? quota / (double)UsenetServer.BytesPerGb : null,
        s.QuotaBytes is null ? 0 : s.QuotaUsedBytes,
        s.QuotaSince is { } since ? new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)) : null,
        s.Notes);
}

/// <summary>
/// Body for create, update and test. Missing fields get the defaults of a new server (563, SSL, 20 connections, primary,
/// 60 s timeout, strict certificate check, one article per request, skip on failure, no quota).
/// </summary>
public sealed record UsenetServerRequest(
    int Id,
    string? Name,
    string? Host,
    int? Port,
    bool? UseSsl,
    string? Username,
    string? Password,
    int? Connections,
    int? Priority,
    bool? Enabled,
    int? RetentionDays,
    int? TimeoutSeconds = null,
    string? CertificateCheck = null,
    int? ArticlesPerRequest = null,
    string? OnFailure = null,
    string? ExpiresOn = null,
    double? QuotaGb = null,
    string? Notes = null)
{
    /// <exception cref="ValidationFailedException">Unknown certificate check or failure action, or an impossible quota.</exception>
    public UsenetServer ToServer()
    {
        var defaults = UsenetServerService.Schema();
        var errors = new Dictionary<string, string[]>();
        var check = defaults.CertificateCheck;
        if (CertificateCheck is { } checkText && !UsenetServerValues.TryParse(checkText, out check))
        {
            errors["certificateCheck"] = ["Use strict, minimal or off."];
        }

        var onFailure = defaults.OnFailure;
        if (OnFailure is { } failureText && !UsenetServerValues.TryParse(failureText, out onFailure))
        {
            errors["onFailure"] = ["Use skip, pause or ignore."];
        }

        long? quota = null;
        if (QuotaGb is { } gb)
        {
            if (!double.IsFinite(gb) || gb <= 0 || gb > UsenetServerService.MaxQuotaGb)
            {
                errors["quotaGb"] = [
                    $"Use more than 0 and at most {UsenetServerService.MaxQuotaGb.ToString("N0", CultureInfo.InvariantCulture)} GB, or leave it empty for no quota."];
            }
            else
            {
                quota = Math.Max(1, (long)Math.Round(gb * UsenetServer.BytesPerGb));
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return new UsenetServer(
            Id,
            Name ?? string.Empty,
            Host ?? string.Empty,
            Port ?? defaults.Port,
            UseSsl ?? defaults.UseSsl,
            Username ?? string.Empty,
            Password ?? string.Empty,
            Connections ?? defaults.Connections,
            Priority ?? defaults.Priority,
            Enabled ?? defaults.Enabled,
            RetentionDays ?? defaults.RetentionDays,
            TimeoutSeconds ?? defaults.TimeoutSeconds,
            check,
            ArticlesPerRequest ?? defaults.ArticlesPerRequest,
            onFailure,
            ExpiresOn,
            quota,
            Notes: Notes ?? string.Empty);
    }
}

/// <summary>API names of the server enums.</summary>
public static class UsenetServerValues
{
    public static string Name(CertificateCheck check) => check switch
    {
        CertificateCheck.Minimal => "minimal",
        CertificateCheck.Off => "off",
        _ => "strict",
    };

    public static string Name(ServerFailureAction action) => action switch
    {
        ServerFailureAction.PauseDownloads => "pause",
        ServerFailureAction.IgnoreLonger => "ignore",
        _ => "skip",
    };

    public static bool TryParse(string text, out CertificateCheck check)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (text.Trim().ToLowerInvariant())
        {
            case "strict":
                check = CertificateCheck.Strict;
                return true;
            case "minimal":
                check = CertificateCheck.Minimal;
                return true;
            case "off":
                check = CertificateCheck.Off;
                return true;
            default:
                check = CertificateCheck.Strict;
                return false;
        }
    }

    public static bool TryParse(string text, out ServerFailureAction action)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (text.Trim().ToLowerInvariant())
        {
            case "skip":
                action = ServerFailureAction.Skip;
                return true;
            case "pause":
                action = ServerFailureAction.PauseDownloads;
                return true;
            case "ignore":
                action = ServerFailureAction.IgnoreLonger;
                return true;
            default:
                action = ServerFailureAction.Skip;
                return false;
        }
    }
}

public sealed record UsenetTestResource(bool Ok, string Message, int? LatencyMs);
