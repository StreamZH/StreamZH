// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Library.ImportLists;

/// <summary>An import list as sent by the API (create, update, test, preview). Settings may hold masked secrets.</summary>
/// <param name="MediaType">Null: the first media type of the list type.</param>
public sealed record ImportListInput(
    string? Name,
    string? Implementation,
    MediaType? MediaType,
    bool Enabled,
    bool EnableAuto,
    bool SearchOnAdd,
    string? Monitor,
    int RootFolderId,
    int? QualityProfileId,
    string? MinimumAvailability,
    IReadOnlyDictionary<string, JsonElement>? Settings);

/// <summary>Settings → Import lists: validation and storage of the lists, the Test button and the exclusions.</summary>
public sealed class ImportListService(
    ImportListRepository repository,
    RootFolderRepository rootFolders,
    QualityProfileRepository profiles,
    IEnumerable<IImportListSource> sources,
    IEventBus events,
    TimeProvider time,
    ILogger<ImportListService> logger)
{
    public const int MaxNameLength = 100;

    private readonly IReadOnlyList<IImportListValidator> _validators = [.. sources.OfType<IImportListValidator>()];

    public IReadOnlyList<ImportListDefinition> All() => repository.All();

    public ImportListDefinition? Find(int id) => repository.Find(id);

    /// <exception cref="ValidationFailedException">A field is invalid.</exception>
    public ImportListDefinition Create(ImportListInput input)
    {
        var definition = Validate(input, null);
        var id = repository.Add(definition, time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Import list {Name} ({Type}) added", definition.Name, definition.Implementation);
        Changed();
        return repository.Find(id)!;
    }

    /// <exception cref="LibraryException">No list with this id.</exception>
    /// <exception cref="ValidationFailedException">A field is invalid.</exception>
    public ImportListDefinition Update(int id, ImportListInput input)
    {
        var existing = repository.Find(id) ?? throw NotFound(id);
        var definition = Validate(input, existing) with { Id = id };
        var changed = definition.Implementation != existing.Implementation
            || definition.MediaType != existing.MediaType
            || JsonSerializer.Serialize(definition.Settings) != JsonSerializer.Serialize(existing.Settings)
            || !definition.Enabled;
        repository.Update(definition, clearStatus: changed);
        logger.LogInformation("Import list {Name} ({Type}) saved", definition.Name, definition.Implementation);
        Changed();
        return repository.Find(id)!;
    }

    /// <exception cref="LibraryException">No list with this id.</exception>
    public void Delete(int id)
    {
        var existing = repository.Find(id) ?? throw NotFound(id);
        repository.Delete(id);
        logger.LogInformation("Import list {Name} deleted", existing.Name);
        Changed();
    }

    /// <summary>
    /// The definition for Test and Preview: checked like a save, masked secrets from the saved list <paramref name="storedId"/>.
    /// </summary>
    /// <exception cref="ValidationFailedException">A field is invalid.</exception>
    public ImportListDefinition Draft(ImportListInput input, int? storedId)
    {
        var stored = storedId is { } id ? repository.Find(id) : null;
        return Validate(input, stored, checkName: false, test: true) with { Id = stored?.Id ?? 0 };
    }

    // ---- Exclusions ----

    public IReadOnlyList<ImportListExclusion> Exclusions() => repository.Exclusions();

    /// <exception cref="ValidationFailedException">Title or TMDB id missing.</exception>
    public ImportListExclusion AddExclusion(MediaType mediaType, int tmdbId, string? title, int year)
    {
        var errors = new Dictionary<string, string[]>();
        if (mediaType is not (MediaType.Movie or MediaType.Series))
        {
            errors["mediaType"] = ["Use Movie or Series."];
        }

        if (tmdbId <= 0)
        {
            errors["tmdbId"] = ["Enter the TMDB id."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var name = string.IsNullOrWhiteSpace(title) ? $"TMDB {tmdbId}" : title.Trim();
        var id = repository.AddExclusion(mediaType, tmdbId, name.Length > 300 ? name[..300] : name, Math.Max(0, year), time.GetUtcNow().UtcDateTime);
        logger.LogInformation("Import list exclusion added: {Title} ({Year})", name, year);
        return repository.FindExclusion(id)!;
    }

    /// <exception cref="LibraryException">No exclusion with this id.</exception>
    public void DeleteExclusion(int id)
    {
        var existing = repository.FindExclusion(id) ?? throw new LibraryException($"Exclusion {id} not found.", LibraryError.NotFound);
        repository.DeleteExclusion(id);
        logger.LogInformation("Import list exclusion removed: {Title} ({Year})", existing.Title, existing.Year);
    }

    /// <summary>The list that added this library item, null when it was added another way.</summary>
    public ImportListOrigin? Origin(MediaType mediaType, int tmdbId) => repository.FindOrigin(mediaType, tmdbId);

    private ImportListDefinition Validate(ImportListInput input, ImportListDefinition? stored, bool checkName = true, bool test = false)
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
            errors["name"] = [$"A list named {other.Name} already exists."];
        }

        var type = ImportListCatalog.Find(input.Implementation);
        if (type is null)
        {
            errors["implementation"] = [$"Use one of: {string.Join(", ", ImportListCatalog.All.Select(t => t.Key))}."];
            throw new ValidationFailedException(errors);
        }

        var mediaType = input.MediaType ?? type.MediaTypes[0];
        if (!type.MediaTypes.Contains(mediaType))
        {
            errors["mediaType"] = [$"{type.Name} can only add {string.Join(" or ", type.MediaTypes.Select(m => m == MediaType.Series ? "series" : "movies"))}."];
            mediaType = type.MediaTypes[0];
        }

        if (stored is not null && !string.Equals(stored.Implementation, type.Key, StringComparison.Ordinal))
        {
            // Another type: secrets of the old one must not carry over.
            stored = null;
        }

        var settings = ConnectionSettings.Normalize(type.AsConnectType(), input.Settings, stored?.Settings, errors, test);
        var typed = new ConnectionSettings(settings);

        // Required fields that only apply to the other media type or to another choice are not required.
        foreach (var field in type.Fields.Where(f => !ImportListCatalog.Applies(f, mediaType, typed.Text)))
        {
            errors.Remove(field.Name);
        }

        foreach (var validator in _validators.Where(v => v is IImportListSource source && source.Implementations.Contains(type.Key)))
        {
            validator.Validate(type.Key, mediaType, typed, errors);
        }

        var monitor = string.IsNullOrWhiteSpace(input.Monitor) ? ImportListMonitor.Default(mediaType) : input.Monitor.Trim();
        if (!ImportListMonitor.For(mediaType).Any(o => o.Value == monitor))
        {
            errors["monitor"] = [$"Use one of: {string.Join(", ", ImportListMonitor.For(mediaType).Select(o => o.Value))}."];
        }

        if (rootFolders.Find(input.RootFolderId) is not { } root)
        {
            errors["rootFolderId"] = [rootFolders.All().Any(r => r.MediaType == mediaType)
                ? "Choose a root folder."
                : $"Add a {(mediaType == MediaType.Series ? "series" : "movie")} root folder under Settings → Media management first."];
        }
        else if (root.MediaType != mediaType)
        {
            errors["rootFolderId"] = [$"This root folder is for {(root.MediaType == MediaType.Series ? "series" : "movies")}."];
        }

        if (input.QualityProfileId is { } profileId && profiles.Find(profileId) is null)
        {
            errors["qualityProfileId"] = ["This quality profile does not exist."];
        }

        var availability = mediaType == MediaType.Series || string.IsNullOrWhiteSpace(input.MinimumAvailability)
            ? ImportListAvailability.Announced
            : input.MinimumAvailability.Trim();
        if (!ImportListAvailability.Options.Any(o => o.Value == availability))
        {
            errors["minimumAvailability"] = ["Use announced, inCinemas or released."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return new ImportListDefinition(
            stored?.Id ?? 0,
            name,
            type.Key,
            mediaType,
            input.Enabled,
            input.EnableAuto,
            input.SearchOnAdd,
            monitor,
            input.RootFolderId,
            input.QualityProfileId,
            availability,
            settings);
    }

    /// <summary>The status window lists failing lists; it re-reads after any change.</summary>
    private void Changed() => _ = events.PublishAsync(new OngoingChanged());

    private static LibraryException NotFound(int id) => new($"Import list {id} not found.", LibraryError.NotFound);
}
