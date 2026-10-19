// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Diagnostics;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>Turns expected domain errors into problem responses with a readable message.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ValidationFailedException validation)
        {
            logger.LogDebug("API validation error: {Fields}", string.Join(", ", validation.Errors.Keys));
            var extensions = new Dictionary<string, object?>();
            if (validation.Code is { } validationCode)
            {
                extensions["code"] = validationCode;
            }

            if (validation.Paths is { Count: > 0 } validationPaths)
            {
                extensions["paths"] = validationPaths;
            }

            await Results.ValidationProblem(validation.Errors, extensions: extensions).ExecuteAsync(http);
            return true;
        }

        var (status, code) = exception switch
        {
            LibraryException { Error: LibraryError.NotFound } => (StatusCodes.Status404NotFound, "not-found"),
            LibraryException { Error: LibraryError.AlreadyExists } => (StatusCodes.Status409Conflict, "already-exists"),
            LibraryException { Error: LibraryError.InUse } => (StatusCodes.Status409Conflict, "in-use"),
            LibraryException { Error: LibraryError.Conflict } => (StatusCodes.Status409Conflict, "conflict"),
            LibraryException { Error: LibraryError.OutsideStorage } => (StatusCodes.Status400BadRequest, StreamZH.Core.AppPaths.OutsideStorageCode),
            LibraryException => (StatusCodes.Status400BadRequest, "invalid"),
            TmdbException { StatusCode: null } when exception.Message.StartsWith("TMDB is not set up", StringComparison.Ordinal)
                => (StatusCodes.Status400BadRequest, "tmdb-not-configured"),
            TmdbException { StatusCode: 401 } => (StatusCodes.Status400BadRequest, "tmdb-unauthorized"),
            TmdbException { StatusCode: 404 } => (StatusCodes.Status404NotFound, "tmdb-not-found"),
            TmdbException => (StatusCodes.Status502BadGateway, "tmdb-error"),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        logger.LogDebug("API error {Status}: {Message}", status, exception.Message);
        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = { Status = status, Title = exception.Message, Detail = exception.Message, Extensions = { ["code"] = code } },
        });
    }
}