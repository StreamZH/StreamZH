// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Api;

/// <summary>Turns expected domain errors into problem responses with a readable message.</summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// The problem details, this handler, and (1.0 RC) request bodies and parameters that cannot be read (malformed JSON,
    /// an unknown enum value, a number for a text, a missing body) as 400 with a field error instead of an empty 400 or,
    /// while developing, a 500: minimal APIs throw <see cref="BadHttpRequestException"/> and this handler answers it.
    /// </summary>
    public static IServiceCollection Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is BadHttpRequestException badRequest)
        {
            logger.LogDebug("Bad request {Status}: {Message}", badRequest.StatusCode, badRequest.Message);
            if (badRequest.StatusCode == StatusCodes.Status400BadRequest)
            {
                await Results.ValidationProblem(BadRequestErrors(badRequest)).ExecuteAsync(http);
                return true;
            }

            var message = badRequest.StatusCode == StatusCodes.Status415UnsupportedMediaType ? ApiFallback.JsonMessage : badRequest.Message;
            http.Response.StatusCode = badRequest.StatusCode;
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails = { Status = badRequest.StatusCode, Title = message, Detail = message },
            });
        }

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

    /// <summary>
    /// The field errors of a request that could not be read, keyed like the other validation errors (camelCase field
    /// names, "items[0].kind" for nested ones, "" for the body as a whole).
    /// </summary>
    public static Dictionary<string, string[]> BadRequestErrors(BadHttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.InnerException is JsonException json)
        {
            var field = FieldName(json.Path);
            var syntax = json.InnerException?.GetType().Name.Contains("Reader", StringComparison.Ordinal) == true || json.BytePositionInLine is not null && field.Length == 0;
            if (syntax || field.Length == 0)
            {
                return new() { [string.Empty] = ["The request body is not valid JSON."] };
            }

            return new() { [field] = [ValueProblem(json.Message, json.Path)] };
        }

        var message = exception.Message;
        if (message.StartsWith("Implicit body inferred", StringComparison.Ordinal) || message.Contains("from body", StringComparison.Ordinal)
            || exception.InnerException is IOException)
        {
            return new() { [string.Empty] = ["Send the request body as JSON."] };
        }

        if (ParameterRegex().Match(message) is { Success: true } parameter)
        {
            var name = parameter.Groups["name"].Value;
            return new() { [name] = [message.StartsWith("Required", StringComparison.Ordinal) ? "This value is required." : "This value is not valid."] };
        }

        return new() { [string.Empty] = ["The request could not be read."] };
    }

    /// <summary>"$.mediaType" → "mediaType", "$.items[0].kind" → "items[0].kind", "$" → "".</summary>
    public static string FieldName(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return string.Empty;
        }

        var field = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
        return field.Length > 0 ? JsonNamingPolicy.CamelCase.ConvertName(field) : string.Empty;
    }

    /// <summary>"Use one of: Movie, Series." for an enum, else a plain "not valid".</summary>
    private static string ValueProblem(string message, string? path)
    {
        if (TargetTypeRegex().Match(message) is { Success: true } match && FindType(match.Groups["type"].Value) is { } type
            && (type.IsEnum ? type : PropertyType(type, path)) is { IsEnum: true } target)
        {
            return $"This value is not valid. Use one of: {string.Join(", ", Enum.GetNames(target))}.";
        }

        return "This value is not valid.";
    }

    /// <summary>
    /// The type of the property at <paramref name="path"/> ("$.items[0].kind") below <paramref name="type"/>: for
    /// constructor parameters (records) System.Text.Json names the record, not the property, in its message.
    /// </summary>
    private static Type? PropertyType(Type type, string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return null;
        }

        Type? current = type;
        foreach (var segment in path.TrimStart('$').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket >= 0 ? segment[..bracket] : segment;
            current = current?.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)?.PropertyType;
            if (current is not null && bracket >= 0)
            {
                current = current.IsArray ? current.GetElementType() : current.IsGenericType ? current.GetGenericArguments()[^1] : null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current is null ? null : Nullable.GetUnderlyingType(current) ?? current;
    }

    private static Type? FindType(string name)
    {
        // Nullable`1[[Namespace.Type, Assembly, …]] → Namespace.Type
        var inner = NullableRegex().Match(name);
        var full = inner.Success ? inner.Groups["inner"].Value : name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.StartsWith("StreamZH", StringComparison.Ordinal) == true && assembly.GetType(full) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    [GeneratedRegex(@"could not be converted to (?<type>[^\s.]+(?:\.[^\s.]+)*?)\.(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TargetTypeRegex();

    [GeneratedRegex(@"Nullable`1\[\[?(?<inner>[^,\]\[]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NullableRegex();

    [GeneratedRegex(@"parameter ""(?<type>[^""]+?) (?<name>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();
}

public static class ApiErrorExtensions
{
    /// <summary>See <see cref="ApiExceptionHandler.Register"/>.</summary>
    public static IServiceCollection AddApiErrors(this IServiceCollection services) => ApiExceptionHandler.Register(services);
}
