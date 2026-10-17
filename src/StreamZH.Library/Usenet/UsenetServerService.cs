// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Usenet.Nntp;
using StreamZH.Usenet.Pool;

namespace StreamZH.Library.Usenet;

/// <summary>Outcome of the "Test" button. <see cref="LatencyMs"/> is the round trip of one command on the open connection.</summary>
public sealed record UsenetServerTestResult(bool Ok, string Message, int? LatencyMs);

/// <summary>Stores and validates Usenet servers, tests them, and keeps the connection pool in line with the stored list.</summary>
public sealed class UsenetServerService(
    UsenetServerRepository servers,
    UsenetArticleSource source,
    TimeProvider time,
    ILogger<UsenetServerService> logger,
    IEventBus? events = null,
    UsenetPoolOptions? options = null)
{
    public const int MaxNameLength = 100;
    public const int MaxHostLength = 255;
    public const int DefaultPort = 563;
    public const int DefaultConnections = 20;
    public const int MinConnections = 1;
    public const int MaxConnections = 100;
    public const int MaxPriority = 99;

    /// <summary>What the API shows instead of a stored password. Sent back unchanged, it means "keep the stored password".</summary>
    public const string MaskedPassword = "********";

    /// <summary>A new server to start from: SSL on port 563, 20 connections, primary.</summary>
    public static UsenetServer Schema() => new(0, string.Empty, string.Empty, DefaultPort, true, string.Empty, string.Empty, DefaultConnections, 0, true, 0);

    /// <summary>All servers ordered by priority, then name.</summary>
    public IReadOnlyList<UsenetServer> All() => servers.All();

    public UsenetServer? Find(int id) => servers.Find(id);

    public UsenetServer Create(UsenetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var normalized = Normalize(server) with { Id = 0 };
        var errors = Validate(normalized, null);
        if (normalized.Password == MaskedPassword)
        {
            errors["password"] = ["Enter the password."];
        }

        ThrowIfAny(errors);
        var id = servers.Add(normalized, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Usenet server '{Name}' ({Host}) added", normalized.Name, normalized.Host);
        Changed();
        return servers.Find(id)!;
    }

    public UsenetServer Update(int id, UsenetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var existing = servers.Find(id) ?? throw new LibraryException("Usenet server not found.", LibraryError.NotFound);
        var normalized = Normalize(server) with { Id = id };
        if (normalized.Password == MaskedPassword)
        {
            normalized = normalized with { Password = existing.Password };
        }

        ThrowIfAny(Validate(normalized, id));
        servers.Update(normalized);
        logger.LogInformation("Usenet server '{Name}' ({Host}) saved", normalized.Name, normalized.Host);
        Changed();
        return servers.Find(id)!;
    }

    public void Delete(int id)
    {
        var existing = servers.Find(id) ?? throw new LibraryException("Usenet server not found.", LibraryError.NotFound);
        servers.Delete(id);
        logger.LogInformation("Usenet server '{Name}' deleted", existing.Name);
        Changed();
    }

    /// <summary>Hands the stored servers to the connection pool (on start and after every change).</summary>
    public void Reload() => source.Configure(servers.All());

    /// <summary>Live state of every stored server (disabled ones with no connections), by priority, plus the total speed.</summary>
    public UsenetPoolStatus Status()
    {
        var live = source.Status();
        var byId = live.Servers.ToDictionary(s => s.Id);
        var list = servers.All()
            .Select(s => byId.GetValueOrDefault(s.Id) is { } status
                ? status with { Name = s.Name }
                : new UsenetServerStatus(s.Id, s.Name, 0, s.Connections, true, null, source.BytesToday(s.Id)))
            .ToList();
        return live with { Servers = list };
    }

    /// <summary>
    /// Connects, logs in and sends DATE without saving anything. A masked password with the id of a stored server uses its password.
    /// Throws <see cref="ValidationFailedException"/> for unusable input; connection problems come back as a failed result.
    /// </summary>
    public async Task<UsenetServerTestResult> TestAsync(UsenetServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var normalized = Normalize(server);
        var errors = ValidateConnection(normalized);
        if (normalized.Password == MaskedPassword)
        {
            if (normalized.Id > 0 && servers.Find(normalized.Id) is { } stored)
            {
                normalized = normalized with { Password = stored.Password };
            }
            else
            {
                errors["password"] = ["Enter the password."];
            }
        }

        ThrowIfAny(errors);
        var settings = (options ?? new UsenetPoolOptions()).SettingsFor(normalized);
        var endpoint = $"{normalized.Host}:{normalized.Port}";
        try
        {
            await using var connection = await NntpConnection.ConnectAsync(settings, cancellationToken);
            var started = time.GetTimestamp();
            await connection.DateAsync(cancellationToken);
            var latency = (int)Math.Round(time.GetElapsedTime(started).TotalMilliseconds);
            await connection.QuitAsync();

            var how = connection.TlsProtocol is { } tls ? $"Connected with {tls}" : "Connected without SSL";
            var login = string.IsNullOrEmpty(normalized.Username) ? "no login needed" : "logged in";
            logger.LogInformation("Usenet server test for {Endpoint} succeeded ({Latency} ms)", endpoint, latency);
            return new UsenetServerTestResult(true, $"{how}, {login}. The server answered in {latency} ms.", latency);
        }
        catch (NntpException ex)
        {
            logger.LogInformation("Usenet server test for {Endpoint} failed: {Message}", endpoint, ex.Message);
            var text = ex.Message.TrimEnd();
            var sentence = text.Length > 0 && char.IsPunctuation(text[^1]) ? text : text + ".";
            var message = ex.Kind switch
            {
                NntpErrorKind.Auth => $"{sentence} Check the user name and password.",
                NntpErrorKind.Busy => $"{sentence} The account may be in use by another downloader.",
                _ => sentence,
            };
            return new UsenetServerTestResult(false, message, null);
        }
    }

    private void Changed()
    {
        Reload();
        if (events is not null)
        {
            _ = PublishAsync(events);
        }
    }

    // The status window shows a notice while no server is set up.
    private async Task PublishAsync(IEventBus bus)
    {
        try
        {
            await bus.PublishAsync(new OngoingChanged());
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not publish the Usenet server change: {Message}", ex.Message);
        }
    }

    // ---- Validation ----

    private Dictionary<string, string[]> Validate(UsenetServer server, int? existingId)
    {
        var errors = ValidateConnection(server);

        if (string.IsNullOrWhiteSpace(server.Name))
        {
            errors["name"] = ["Enter a name."];
        }
        else if (server.Name.Length > MaxNameLength)
        {
            errors["name"] = [$"Use at most {MaxNameLength} characters."];
        }
        else if (servers.FindByName(server.Name) is { } other && other.Id != existingId)
        {
            errors["name"] = ["A server with this name already exists."];
        }

        if (server.Connections is < MinConnections or > MaxConnections)
        {
            errors["connections"] = [$"Use {MinConnections} to {MaxConnections} connections. Your provider's plan says how many are allowed."];
        }

        if (server.Priority is < 0 or > MaxPriority)
        {
            errors["priority"] = [$"Use 0 (primary) to {MaxPriority}."];
        }

        if (server.RetentionDays < 0)
        {
            errors["retentionDays"] = ["Use 0 (unknown) or more days."];
        }

        return errors;
    }

    /// <summary>The fields needed to reach the server: host, port and the login.</summary>
    private static Dictionary<string, string[]> ValidateConnection(UsenetServer server)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(server.Host))
        {
            errors["host"] = ["Enter the server address, e.g. news.example.com."];
        }
        else if (server.Host.Length > MaxHostLength || server.Host.Contains("://", StringComparison.Ordinal)
            || Uri.CheckHostName(server.Host) == UriHostNameType.Unknown)
        {
            errors["host"] = ["Enter only the host name, e.g. news.example.com (no nntp:// and no port)."];
        }

        if (server.Port is < 1 or > 65535)
        {
            errors["port"] = ["Use a port from 1 to 65535 (usually 563 with SSL, 119 without)."];
        }

        if (server.Username.Any(char.IsControl))
        {
            errors["username"] = ["Remove line breaks and other control characters."];
        }
        else if (string.IsNullOrEmpty(server.Username) && !string.IsNullOrEmpty(server.Password))
        {
            errors["username"] = ["Enter the user name for this password."];
        }

        if (server.Password.Any(char.IsControl))
        {
            errors["password"] = ["Remove line breaks and other control characters."];
        }

        return errors;
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    /// <summary>Trims text fields except the password (spaces can be part of it).</summary>
    private static UsenetServer Normalize(UsenetServer server) => server with
    {
        Name = server.Name?.Trim() ?? string.Empty,
        Host = server.Host?.Trim() ?? string.Empty,
        Username = server.Username?.Trim() ?? string.Empty,
        Password = server.Password ?? string.Empty,
    };
}
