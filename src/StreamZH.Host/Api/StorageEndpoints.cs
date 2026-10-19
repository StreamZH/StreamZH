// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Host.Auth;
using StreamZH.Library;

namespace StreamZH.Host.Api;

/// <summary>
/// Settings → General → Storage (0.12, SV2 N-03): the storage locations. Admins see them (folders they set must be inside
/// one); only the owner adds and removes them.
/// </summary>
public static class StorageEndpoints
{
    public static IServiceCollection AddStorageLocations(this IServiceCollection services) => services.AddSingleton<StorageLocationService>();

    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var storage = app.MapGroup("/api/v1/settings/storage").WithTags("Settings");

        storage.MapGet("/", (HttpContext http, StorageLocationService service, AppPaths paths) => Resource(http, service, paths))
            .RequireAuthorization(AuthSchemes.AdminPolicy);

        storage.MapPost("/", (StorageLocationRequest request, HttpContext http, StorageLocationService service, AppPaths paths) =>
        {
            service.Add(request.Path);
            return Resource(http, service, paths);
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);

        storage.MapDelete("/", (string? path, HttpContext http, StorageLocationService service, AppPaths paths) =>
        {
            service.Remove(path);
            return Resource(http, service, paths);
        }).RequireAuthorization(AuthSchemes.OwnerPolicy);

        return app;
    }

    private static StorageResource Resource(HttpContext http, StorageLocationService service, AppPaths paths) =>
        new(service.All(), GeneralSettingsEndpoints.IsOwner(http.User), paths.DownloadsDirectory);
}

public sealed record StorageLocationRequest(string? Path);

/// <param name="CanChange">Only the owner adds and removes storage locations.</param>
/// <param name="DefaultDownloadFolder">The default download folder (&lt;data&gt;/downloads), for the hint.</param>
public sealed record StorageResource(IReadOnlyList<StorageLocationInfo> Locations, bool CanChange, string DefaultDownloadFolder);
