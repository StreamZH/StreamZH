// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;

namespace StreamZH.Library.Notifications;

/// <summary>A connection as sent by the API (create, update, test). Settings may hold masked secrets.</summary>
public sealed record ConnectionInput(
    string? Name,
    string? Implementation,
    bool Enable,
    ConnectionTriggers Triggers,
    IReadOnlyDictionary<string, JsonElement>? Settings);

/// <summary>Settings → Connect: validation, storage and the Test button of media servers and notification services.</summary>
public sealed class ConnectionService(
    ConnectionRepository repository,
    IEnumerable<IConnectionSender> senders,
    ConfigRepository config,
    IEventBus events,
    TimeProvider time,
    ILogger<ConnectionService> logger)
{
    public const int MaxNameLength = 100;

    private readonly Dictionary<string, IConnectionSender> _senders =
        senders.ToDictionary(s => s.Implementation, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ConnectionDefinition> All() => repository.All();

    public ConnectionDefinition? Find(int id) => repository.Find(id);

    public IConnectionSender? Sender(string implementation) => _senders.GetValueOrDefault(implementation);

    public string InstanceName => config.Get(ConfigKeys.InstanceName) is { Length: > 0 } name ? name : "StreamZH";

    public ConnectionContext Context(ConnectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ConnectionContext(definition, new ConnectionSettings(definition.Settings), InstanceName);
    }

    /// <exception cref="ValidationFailedException">Name, type, triggers or a setting are invalid.</exception>
    public ConnectionDefinition Create(ConnectionInput input)
    {
        var definition = Validate(input, null);
        var id = repository.Add(definition, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Connection {Name} ({Type}) added", definition.Name, definition.Implementation);
        Changed();
        return repository.Find(id)!;
    }

    /// <exception cref="LibraryException">No connection with this id.</exception>
    /// <exception cref="ValidationFailedException">Name, type, triggers or a setting are invalid.</exception>
    public ConnectionDefinition Update(int id, ConnectionInput input)
    {
        var existing = repository.Find(id) ?? throw NotFound(id);
        var definition = Validate(input, existing) with { Id = id };
        repository.Update(definition);
        logger.LogInformation("Connection {Name} ({Type}) saved", definition.Name, definition.Implementation);
        Changed();
        return repository.Find(id)!;
    }

    /// <exception cref="LibraryException">No connection with this id.</exception>
    public void Delete(int id)
    {
        var existing = repository.Find(id) ?? throw NotFound(id);
        repository.Delete(id);
        logger.LogInformation("Connection {Name} deleted", existing.Name);
        Changed();
    }

    /// <summary>
    /// Tests the settings without saving them. Masked secrets come from the saved connection <paramref name="storedId"/>.
    /// A successful test of a saved connection clears its error.
    /// </summary>
    /// <exception cref="ValidationFailedException">A setting is invalid.</exception>
    /// <exception cref="ConnectException">The test failed.</exception>
    public async Task<string> TestAsync(ConnectionInput input, int? storedId, CancellationToken cancellationToken)
    {
        var stored = storedId is { } id ? repository.Find(id) : null;
        var definition = Validate(input, stored, checkName: false);
        var sender = Sender(definition.Implementation) ?? throw new ConnectException("this type is not supported");
        var message = await sender.TestAsync(new ConnectionContext(definition with { Id = stored?.Id ?? 0 }, new ConnectionSettings(definition.Settings), InstanceName), cancellationToken);
        if (stored is { Status.Failing: true } && SameSettings(stored, definition))
        {
            repository.SetSuccess(stored.Id, time.GetUtcNow().UtcDateTime);
            Changed();
        }

        return message;
    }

    private static bool SameSettings(ConnectionDefinition a, ConnectionDefinition b) =>
        a.Implementation == b.Implementation && ConnectionRepository.SerializeSettings(a.Settings) == ConnectionRepository.SerializeSettings(b.Settings);

    private ConnectionDefinition Validate(ConnectionInput input, ConnectionDefinition? stored, bool checkName = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new Dictionary<string, string[]>();
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"Use 1 to {MaxNameLength} characters."];
        }
        else if (checkName && repository.FindByName(name) is { } other && other.Id != stored?.Id)
        {
            errors["name"] = [$"A connection named {other.Name} already exists."];
        }

        var type = ConnectCatalog.Find(input.Implementation);
        if (type is null)
        {
            errors["implementation"] = [$"Use one of: {string.Join(", ", ConnectCatalog.All.Select(t => t.Key))}."];
            throw new ValidationFailedException(errors);
        }

        if (stored is not null && !string.Equals(stored.Implementation, type.Key, StringComparison.Ordinal))
        {
            // Another type: secrets of the old one must not carry over.
            stored = null;
        }

        var settings = ConnectionSettings.Normalize(type, input.Settings, stored?.Settings, errors);
        if (Sender(type.Key) is IConnectionValidator validator)
        {
            validator.Validate(new ConnectionSettings(settings), errors);
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        // Triggers the type cannot react to are switched off instead of failing.
        var t = input.Triggers ?? new ConnectionTriggers();
        bool On(bool value, string trigger) => value && type.Triggers.Contains(trigger);
        var triggers = new ConnectionTriggers(
            On(t.OnGrab, ConnectTriggers.Grab),
            On(t.OnImport, ConnectTriggers.Import),
            On(t.OnUpgrade, ConnectTriggers.Upgrade),
            On(t.OnRename, ConnectTriggers.Rename),
            On(t.OnDownloadFailure, ConnectTriggers.DownloadFailure),
            On(t.OnHealthIssue, ConnectTriggers.HealthIssue),
            On(t.OnHealthRestored, ConnectTriggers.HealthRestored),
            t.IncludeHealthWarnings && (type.Triggers.Contains(ConnectTriggers.HealthIssue) || type.Triggers.Contains(ConnectTriggers.HealthRestored)),
            On(t.OnRequestCreated, ConnectTriggers.RequestCreated),
            On(t.OnRequestApproved, ConnectTriggers.RequestApproved),
            On(t.OnRequestDeclined, ConnectTriggers.RequestDeclined),
            On(t.OnRequestAvailable, ConnectTriggers.RequestAvailable));

        return new ConnectionDefinition(stored?.Id ?? 0, name, type.Key, input.Enable, triggers, settings);
    }

    /// <summary>The status window lists failing connections; it re-reads after any change.</summary>
    private void Changed() => _ = events.PublishAsync(new OngoingChanged());

    private static LibraryException NotFound(int id) => new($"Connection {id} not found.", LibraryError.NotFound);
}
