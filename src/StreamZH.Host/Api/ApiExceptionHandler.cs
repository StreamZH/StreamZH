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
        var (status, code) = exception switch
        {
            LibraryException { Error: LibraryError.NotFound } => (StatusCodes.Status404NotFound, "not-found"),
            LibraryException { Error: LibraryError.AlreadyExists } => (StatusCodes.Status409Conflict, "already-exists"),
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