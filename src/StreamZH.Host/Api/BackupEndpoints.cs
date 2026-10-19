// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Http.Features;
using StreamZH.Host.Auth;
using StreamZH.Host.Backups;

namespace StreamZH.Host.Api;

/// <summary>
/// System → Backup: list, create, download, delete and restore backups. A restore is checked and staged, and applied at
/// the next (re)start before the database is opened.
/// </summary>
public static class BackupEndpoints
{
    /// <summary>Largest upload for a restore.</summary>
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        // 0.12: owner only. A backup holds the database with every secret and the key ring; a restore replaces the accounts.
        var group = app.MapGroup("/api/v1/system/backup").WithTags("System").RequireAuthorization(AuthSchemes.OwnerPolicy);

        group.MapGet("/", (BackupService backups, BackupSettings settings) =>
            new BackupList(backups.List(), backups.Pending(), settings.Folder, settings.IntervalDays, settings.RetentionDays));

        group.MapPost("/", (BackupService backups) =>
        {
            try
            {
                return Results.Ok(backups.Create(BackupType.Manual));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.Problem($"The backup could not be created: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapGet("/{type}/{name}", (string type, string name, BackupService backups) =>
            backups.PathOf(type, name) is { } path ? Results.File(path, "application/zip", name) : Results.NotFound());

        group.MapDelete("/{type}/{name}", (string type, string name, BackupService backups) =>
            backups.Delete(type, name) ? Results.NoContent() : Results.NotFound());

        // Restore an existing backup.
        group.MapPost("/{type}/{name}/restore", (string type, string name, BackupService backups) =>
        {
            if (backups.PathOf(type, name) is not { } path)
            {
                return Results.NotFound();
            }

            return backups.StageRestore(path, name) is { } problem
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [problem] })
                : Results.Ok(backups.Pending());
        });

        // Restore an uploaded zip: the raw file as the request body (Content-Type application/zip), name in ?name=.
        group.MapPost("/restore", async (HttpContext http, BackupService backups, string? name, CancellationToken ct) =>
        {
            // 0.12 (security review S1-01): only application/zip, a type other sites cannot send without CORS consent
            // (text/plain bodies can come from any web page).
            if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Send the backup as the request body with Content-Type: application/zip.", statusCode: StatusCodes.Status415UnsupportedMediaType);
            }

            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MaxUploadBytes;
            }

            var upload = backups.UploadPath();
            try
            {
                await using (var file = File.Create(upload))
                {
                    await http.Request.Body.CopyToAsync(file, ct);
                }

                if (new FileInfo(upload).Length == 0)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Choose a backup zip file."] });
                }

                var source = string.IsNullOrWhiteSpace(name) ? "uploaded file" : Path.GetFileName(name);
                return backups.StageRestore(upload, source) is { } problem
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [problem] })
                    : Results.Ok(backups.Pending());
            }
            finally
            {
                File.Delete(upload);
            }
        }).DisableAntiforgery();

        group.MapDelete("/restore", (BackupService backups) => backups.CancelRestore() ? Results.NoContent() : Results.NotFound());

        return app;
    }
}

public sealed record BackupList(IReadOnlyList<BackupInfo> Backups, PendingRestoreInfo? PendingRestore, string Folder, int IntervalDays, int RetentionDays);
