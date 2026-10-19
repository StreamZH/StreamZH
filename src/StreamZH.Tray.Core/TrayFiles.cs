// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core;
using StreamZH.Core.Tray;

namespace StreamZH.Tray.Core;

/// <summary>Why the token could not be read.</summary>
public enum TokenProblem
{
    None,

    /// <summary>tray.token does not exist yet (StreamZH never ran with tray support, or the data folder is another one).</summary>
    Missing,

    /// <summary>The file is there but this Windows user may not read it.</summary>
    NoAccess,
}

/// <summary>What the tray read from the data folder: where StreamZH listens and the token.</summary>
public sealed record TrayConnection(string DataDirectory, TrayEndpoint Endpoint, bool EndpointKnown, string? Token, TokenProblem TokenProblem)
{
    public string LogDirectory => string.IsNullOrWhiteSpace(Endpoint.LogDirectory) ? Path.Combine(DataDirectory, "logs") : Endpoint.LogDirectory;
}

/// <summary>Reads tray.json and tray.token (written by StreamZH at every start) from the data folder.</summary>
public static class TrayFiles
{
    public const int DefaultPort = 9797;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The data folder of StreamZH: --data &lt;dir&gt; (the Run entry passes the one of the service), STREAMZH_DATA, else the default.</summary>
    public static string DataDirectory(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? explicitDirectory = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if ((arg is "--data" or "/data" or "-data") && i + 1 < args.Count)
            {
                explicitDirectory = args[i + 1];
                i++;
            }
            else if (arg.StartsWith("--data=", StringComparison.Ordinal))
            {
                explicitDirectory = arg["--data=".Length..];
            }
        }

        return AppPaths.Resolve(explicitDirectory).DataDirectory;
    }

    /// <summary>The address and token; defaults (http://127.0.0.1:9797) when StreamZH has not written tray.json yet.</summary>
    public static TrayConnection Load(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        var endpoint = ReadEndpoint(Path.Combine(dataDirectory, TrayContract.EndpointFile));
        var known = endpoint is not null;
        endpoint ??= Default(dataDirectory);

        string? token = null;
        var problem = TokenProblem.None;
        try
        {
            token = File.ReadAllText(Path.Combine(dataDirectory, TrayContract.TokenFile)).Trim();
            if (token.Length == 0)
            {
                token = null;
                problem = TokenProblem.Missing;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            problem = TokenProblem.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            problem = TokenProblem.NoAccess;
        }

        return new TrayConnection(dataDirectory, endpoint, known, token, problem);
    }

    /// <summary>1.0 RC: why the last start failed (startup-error.json), or null; StreamZH deletes the file when it starts.</summary>
    public static StartupError? ReadStartupError(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        try
        {
            var error = JsonSerializer.Deserialize<StartupError>(File.ReadAllText(Path.Combine(dataDirectory, TrayContract.StartupErrorFile)), Json);
            return error is { Summary.Length: > 0, Message.Length: > 0 } ? error : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static TrayEndpoint Default(string dataDirectory) => new(
        $"http://127.0.0.1:{DefaultPort}",
        $"http://localhost:{DefaultPort}/",
        string.Empty,
        string.Empty,
        0,
        Path.Combine(dataDirectory, "logs"),
        DateTimeOffset.MinValue);

    public static TrayEndpoint? ReadEndpoint(string path)
    {
        try
        {
            var endpoint = JsonSerializer.Deserialize<TrayEndpoint>(File.ReadAllText(path), Json);
            return endpoint is not null && Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
                ? endpoint with { Url = endpoint.Url.TrimEnd('/') }
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
