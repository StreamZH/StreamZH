// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Security;
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
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 300;
    public const int MaxArticlesPerRequest = 20;
    public const int MaxNotesLength = 2000;

    /// <summary>Largest quota in GB (1 PB); anything above is a typo.</summary>
    public const long MaxQuotaGb = 1_000_000;

    /// <summary>What the API shows instead of a stored password. Sent back unchanged, it means "keep the stored password".</summary>
    public const string MaskedPassword = "********";

    /// <summary>Same host, port and SSL setting: where a stored password may be sent again (0.12).</summary>
    public static bool SameDestination(UsenetServer stored, UsenetServer changed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(changed);
        return SecretReuse.SameHost(stored.Host, changed.Host) && stored.Port == changed.Port && stored.UseSsl == changed.UseSsl;
    }

    /// <summary>A new server to start from: SSL on port 563, 20 connections, primary.</summary>
    public static UsenetServer Schema() => new(0, string.Empty, string.Empty, DefaultPort, true, string.Empty, string.Empty, DefaultConnections, 0, true, 0);

    /// <summary>All servers ordered by priority, then name. Quota counters are live (ahead of the stored value).</summary>
    public IReadOnlyList<UsenetServer> All() => [.. servers.All().Select(WithLiveQuota)];

    public UsenetServer? Find(int id) => servers.Find(id) is { } server ? WithLiveQuota(server) : null;

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
        return Find(id)!;
    }

    public UsenetServer Update(int id, UsenetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var existing = servers.Find(id) ?? throw new LibraryException("Usenet server not found.", LibraryError.NotFound);
        var normalized = Normalize(server) with { Id = id };
        var errors = Validate(normalized, id);
        if (normalized.Password == MaskedPassword)
        {
            // 0.12: the stored password only goes to the server it was entered for.
            if (SameDestination(existing, normalized) || string.IsNullOrEmpty(existing.Password))
            {
                normalized = normalized with { Password = existing.Password };
            }
            else
            {
                errors["password"] = [SecretReuse.ChangedAddressMessage("Password", test: false)];
            }
        }

        ThrowIfAny(errors);
        servers.Update(normalized);

        // The counter starts when a quota is set and is dropped when it is removed; changing the amount keeps it.
        if ((existing.QuotaBytes is null) != (normalized.QuotaBytes is null))
        {
            DateTime? since = normalized.QuotaBytes is null ? null : time.GetUtcNow().UtcDateTime;
            source.ResetQuota(id, () => servers.ResetQuota(id, since));
        }

        logger.LogInformation("Usenet server '{Name}' ({Host}) saved", normalized.Name, normalized.Host);
        Changed();
        return Find(id)!;
    }

    /// <summary>Sets the quota counter to zero, counting from now. A server whose quota was used up is used again.</summary>
    /// <exception cref="LibraryException">Not found, or the server has no quota.</exception>
    public UsenetServer ResetQuota(int id)
    {
        var existing = servers.Find(id) ?? throw new LibraryException("Usenet server not found.", LibraryError.NotFound);
        if (existing.QuotaBytes is null)
        {
            throw new LibraryException("This server has no quota.", LibraryError.Invalid);
        }

        var now = time.GetUtcNow().UtcDateTime;
        source.ResetQuota(id, () => servers.ResetQuota(id, now));
        logger.LogInformation("Quota counter of Usenet server '{Name}' reset", existing.Name);
        Changed();
        return Find(id)!;
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
                : new UsenetServerStatus(
                    s.Id, s.Name, 0, s.Connections, true, null, source.BytesToday(s.Id), null, s.QuotaBytes is null ? null : source.QuotaUsed(s.Id) ?? s.QuotaUsedBytes))
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
                // 0.12: the stored password only goes to the server it was entered for.
                if (SameDestination(stored, normalized) || string.IsNullOrEmpty(stored.Password))
                {
                    normalized = normalized with { Password = stored.Password };
                }
                else
                {
                    errors["password"] = [SecretReuse.ChangedAddressMessage("Password", test: true)];
                }
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

        if (server.ArticlesPerRequest is < 1 or > MaxArticlesPerRequest)
        {
            errors["articlesPerRequest"] = [$"Use 1 to {MaxArticlesPerRequest}. 1 sends one request at a time; 2 is what SABnzbd uses."];
        }

        if (!Enum.IsDefined(server.OnFailure))
        {
            errors["onFailure"] = ["Choose what happens when the server fails."];
        }

        if (server.ExpiresOn is not null && server.ExpiryDate is null)
        {
            errors["expiresOn"] = ["Enter a date like 2027-01-31."];
        }

        if (server.QuotaBytes is <= 0 or > MaxQuotaGb * UsenetServer.BytesPerGb)
        {
            errors["quotaGb"] = [$"Use more than 0 and at most {MaxQuotaGb.ToString("N0", CultureInfo.InvariantCulture)} GB, or leave it empty for no quota."];
        }

        if (server.Notes.Length > MaxNotesLength)
        {
            errors["notes"] = [$"Use at most {MaxNotesLength} characters."];
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

        if (server.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            errors["timeoutSeconds"] = [$"Use {MinTimeoutSeconds} to {MaxTimeoutSeconds} seconds."];
        }

        if (!Enum.IsDefined(server.CertificateCheck))
        {
            errors["certificateCheck"] = ["Choose Strict, Minimal or Off."];
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
        ExpiresOn = NormalizeDate(server.ExpiresOn),
        Notes = server.Notes?.Trim() ?? string.Empty,
    };

    /// <summary>"2027-01-31" and "2027-01-31T00:00:00Z" become "2027-01-31"; empty becomes null; anything else stays (and fails validation).</summary>
    private static string? NormalizeDate(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>The stored server with the live quota counter of the connection pool.</summary>
    private UsenetServer WithLiveQuota(UsenetServer server) =>
        server.QuotaBytes is not null && source.QuotaUsed(server.Id) is { } used ? server with { QuotaUsedBytes = used } : server;
}
