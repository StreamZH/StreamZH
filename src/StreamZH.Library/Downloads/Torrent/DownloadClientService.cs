// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Security;
using StreamZH.Data;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>
/// Settings → Download clients: validates and stores torrent clients (qBittorrent, Transmission, Deluge, the built-in
/// engine), keeps secrets out of what the API returns (<see cref="Masked"/>), tests a connection and picks the client
/// for a grab (the enabled one with the lowest priority number, like Radarr).
/// </summary>
public sealed partial class DownloadClientService(
    DownloadClientRepository repository,
    IEnumerable<ITorrentClient> implementations,
    ILogger<DownloadClientService> logger)
{
    public const int MinPriority = 1;
    public const int MaxPriority = 50;
    public const int MaxNameLength = 100;
    private const int MaxTextLength = 1000;

    private readonly Dictionary<string, ITorrentClient> _implementations = implementations
        .GroupBy(i => i.Implementation, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A client was added, changed or removed (the tracker re-reads its clients).</summary>
    public event Action? Changed;

    /// <summary>The implementations that can be added: the catalog entries with a registered client.</summary>
    public IReadOnlyList<DownloadClientImplementation> Schema() =>
        [.. DownloadClientCatalog.All.Where(i => _implementations.ContainsKey(i.Implementation))];

    public IReadOnlyList<DownloadClientDefinition> All() => repository.All();

    public DownloadClientDefinition? Find(int id) => repository.Find(id);

    public ITorrentClient? Client(string implementation) => _implementations.GetValueOrDefault(implementation);

    /// <summary>The client for a torrent grab: the given one (when it exists, is enabled and usable), else the enabled one with the lowest priority.</summary>
    public DownloadClientDefinition? ForGrab(int? clientId)
    {
        var usable = repository.All()
            .Where(c => c.Enable && c.Protocol == DownloadProtocol.Torrent && _implementations.ContainsKey(c.Implementation))
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Id)
            .ToList();
        return clientId is { } id ? usable.FirstOrDefault(c => c.Id == id) : usable.FirstOrDefault();
    }

    /// <exception cref="ValidationFailedException">Invalid name, implementation, priority or settings.</exception>
    public DownloadClientDefinition Create(DownloadClientDefinition input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var client = Validate(input, existing: null);
        var id = repository.Add(client);
        logger.LogInformation("Added download client '{Name}' ({Implementation})", client.Name, client.Implementation);
        Changed?.Invoke();
        return repository.Find(id)!;
    }

    /// <summary>Saves the client; a <see cref="DownloadClientCatalog.Masked"/> secret keeps the stored one.</summary>
    /// <exception cref="LibraryException">Not found.</exception>
    /// <exception cref="ValidationFailedException">Invalid fields.</exception>
    public DownloadClientDefinition Update(int id, DownloadClientDefinition input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var existing = repository.Find(id) ?? throw new LibraryException($"Download client {id} not found.", LibraryError.NotFound);
        var client = Validate(input with { Id = id }, existing);
        repository.Update(client);
        logger.LogInformation("Updated download client '{Name}'", client.Name);
        Changed?.Invoke();
        return repository.Find(id)!;
    }

    /// <exception cref="LibraryException">Not found.</exception>
    public void Delete(int id)
    {
        var existing = repository.Find(id) ?? throw new LibraryException($"Download client {id} not found.", LibraryError.NotFound);
        repository.Delete(id);
        logger.LogInformation("Removed download client '{Name}'", existing.Name);
        Changed?.Invoke();
    }

    /// <summary>
    /// Tests the connection with the given settings; masked secrets come from the saved client <paramref name="id"/>.
    /// Nothing is saved.
    /// </summary>
    /// <exception cref="ValidationFailedException">The settings are invalid.</exception>
    /// <exception cref="DownloadClientException">The client could not be reached or refused (message for the user).</exception>
    public async Task<TorrentClientInfo> TestAsync(DownloadClientDefinition input, int? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var existing = id is { } savedId ? repository.Find(savedId) : null;
        var client = Validate(input with { Id = existing?.Id ?? 0 }, existing, checkName: false, test: true);
        return await Client(client.Implementation)!.TestAsync(client, cancellationToken);
    }

    /// <summary>Same host, port, SSL and URL base: where a stored secret may be sent again (0.12).</summary>
    public static bool SameDestination(IReadOnlyDictionary<string, JsonElement> stored, IReadOnlyDictionary<string, JsonElement> changed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(changed);
        static string Value(IReadOnlyDictionary<string, JsonElement> settings, string key) =>
            settings.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText() : string.Empty;

        return SecretReuse.SameHost(Value(stored, DownloadClientCatalog.Host), Value(changed, DownloadClientCatalog.Host))
            && SecretReuse.Same(Value(stored, DownloadClientCatalog.Port), Value(changed, DownloadClientCatalog.Port))
            && SecretReuse.Same(Value(stored, DownloadClientCatalog.UseSsl), Value(changed, DownloadClientCatalog.UseSsl))
            && SecretReuse.Same(Value(stored, DownloadClientCatalog.UrlBase).TrimEnd('/'), Value(changed, DownloadClientCatalog.UrlBase).TrimEnd('/'));
    }

    /// <summary>The client as the API shows it: secrets replaced by <see cref="DownloadClientCatalog.Masked"/> (empty stays empty).</summary>
    public static DownloadClientDefinition Masked(DownloadClientDefinition client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var implementation = DownloadClientCatalog.Find(client.Implementation);
        var settings = new Dictionary<string, JsonElement>(client.Settings, StringComparer.Ordinal);
        foreach (var field in implementation?.Fields.Where(f => f.Secret) ?? [])
        {
            if (settings.TryGetValue(field.Name, out var value) && !field.IsEmpty(value))
            {
                settings[field.Name] = JsonSerializer.SerializeToElement(DownloadClientCatalog.Masked);
            }
        }

        return client with { Settings = settings };
    }

    /// <summary>Checks everything and returns the client as it is stored: known fields only, defaults filled in, secrets resolved.</summary>
    private DownloadClientDefinition Validate(DownloadClientDefinition input, DownloadClientDefinition? existing, bool checkName = true, bool test = false)
    {
        var errors = new Dictionary<string, string[]>();
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors["name"] = ["Enter a name."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"Use at most {MaxNameLength} characters."];
        }
        else if (checkName && repository.FindByName(name) is { } other && other.Id != input.Id)
        {
            errors["name"] = ["A download client with this name exists already."];
        }

        var implementation = DownloadClientCatalog.Find(input.Implementation);
        if (implementation is null || !_implementations.ContainsKey(implementation.Implementation))
        {
            errors["implementation"] = [$"Use one of: {string.Join(", ", Schema().Select(i => i.Implementation))}."];
        }
        else if (existing is not null && !string.Equals(existing.Implementation, implementation.Implementation, StringComparison.OrdinalIgnoreCase))
        {
            errors["implementation"] = ["The kind of a download client cannot be changed. Add a new one instead."];
        }

        if (input.Priority is < MinPriority or > MaxPriority)
        {
            errors["priority"] = [$"Use {MinPriority} (highest) to {MaxPriority}."];
        }

        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var reused = new List<DownloadClientField>();
        if (implementation is not null)
        {
            var given = new Dictionary<string, JsonElement>(input.Settings ?? new Dictionary<string, JsonElement>(), StringComparer.OrdinalIgnoreCase);
            foreach (var field in implementation.Fields)
            {
                var value = given.TryGetValue(field.Name, out var v) && v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? v : field.Default;
                if (field.Secret && value.ValueKind == JsonValueKind.String && value.GetString() == DownloadClientCatalog.Masked)
                {
                    value = existing?.Settings.GetValueOrDefault(field.Name) is { ValueKind: JsonValueKind.String } stored ? stored : field.Default;
                    if (!field.IsEmpty(value))
                    {
                        reused.Add(field);
                    }
                }

                if (Normalize(field, value, out var normalized) is { } problem)
                {
                    errors[field.Name] = [problem];
                    continue;
                }

                settings[field.Name] = normalized;
            }

            CheckSettings(implementation, settings, errors);

            // 0.12: a stored password only goes to the address it was entered for.
            if (reused.Count > 0 && existing is not null && !SameDestination(existing.Settings, settings))
            {
                foreach (var field in reused)
                {
                    errors[field.Name] = [SecretReuse.ChangedAddressMessage(field.Label, test)];
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return new DownloadClientDefinition(
            input.Id, name, implementation!.Implementation, implementation.Protocol, input.Enable, input.Priority, settings)
        {
            RemoveCompleted = input.RemoveCompleted,
        };
    }

    /// <summary>Converts a field value to its type; returns a problem text when it does not fit.</summary>
    private static string? Normalize(DownloadClientField field, JsonElement value, out JsonElement normalized)
    {
        normalized = value;
        switch (field.Type)
        {
            case DownloadClientFieldTypes.Number:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
                {
                    normalized = JsonSerializer.SerializeToElement(n);
                    return null;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    normalized = JsonSerializer.SerializeToElement(parsed);
                    return null;
                }

                return "Enter a whole number.";
            case DownloadClientFieldTypes.Checkbox:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return null;
                }

                return "Use true or false.";
            default:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return "Enter text.";
                }

                var text = field.Secret ? value.GetString()! : value.GetString()!.Trim();
                if (text.Length > MaxTextLength || text.Any(char.IsControl))
                {
                    return "This value is too long or has invalid characters.";
                }

                if (field.Required && text.Length == 0)
                {
                    return $"Enter the {field.Label.ToLowerInvariant()}.";
                }

                normalized = JsonSerializer.SerializeToElement(text);
                return null;
        }
    }

    private static void CheckSettings(DownloadClientImplementation implementation, Dictionary<string, JsonElement> settings, Dictionary<string, string[]> errors)
    {
        string Text(string key) => settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

        if (settings.ContainsKey(DownloadClientCatalog.Host))
        {
            var host = Text(DownloadClientCatalog.Host);
            if (host.Contains("://", StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
            {
                errors[DownloadClientCatalog.Host] = ["Enter only the host name or IP address, without http:// or a path."];
            }
        }

        if (settings.TryGetValue(DownloadClientCatalog.Port, out var port) && port.GetInt32() is < 1 or > 65535)
        {
            errors[DownloadClientCatalog.Port] = ["Use a port between 1 and 65535."];
        }

        if (settings.ContainsKey(DownloadClientCatalog.UrlBase) && !UrlBaseRegex().IsMatch(Text(DownloadClientCatalog.UrlBase)))
        {
            errors[DownloadClientCatalog.UrlBase] = ["Use a path like /qbittorrent (letters, digits, '-', '_', '.' and '/')."];
        }

        var deluge = string.Equals(implementation.Implementation, DownloadClientDefinition.Deluge, StringComparison.OrdinalIgnoreCase);
        foreach (var key in new[] { DownloadClientCatalog.Category, DownloadClientCatalog.TvCategory })
        {
            if (!settings.ContainsKey(key))
            {
                continue;
            }

            var category = Text(key);
            if (deluge ? !DelugeLabelRegex().IsMatch(category) : !CategoryRegex().IsMatch(category))
            {
                errors[key] = [deluge
                    ? "Deluge labels may only use lower case letters, digits, '-' and '_'."
                    : "A category cannot contain '\\' or '//' and cannot start or end with '/'."];
            }
        }

        if (settings.ContainsKey(DownloadClientCatalog.Directory) && Text(DownloadClientCatalog.Directory) is { Length: > 0 } directory
            && !(directory.StartsWith('/') || directory.StartsWith(@"\\", StringComparison.Ordinal) || (directory.Length >= 3 && char.IsAsciiLetter(directory[0]) && directory[1] == ':' && directory[2] is '\\' or '/')))
        {
            errors[DownloadClientCatalog.Directory] = ["Use a full path as the download client sees it, e.g. /downloads/streamzh."];
        }
    }

    [GeneratedRegex(@"^(/?[A-Za-z0-9._~\-]+)*/?$")]
    private static partial Regex UrlBaseRegex();

    // Radarr's rule for qBittorrent categories (also fine for Transmission labels).
    [GeneratedRegex(@"^([^\\/](/?[^\\/])*)?$")]
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"^[a-z0-9_\-]*$")]
    private static partial Regex DelugeLabelRegex();
}
