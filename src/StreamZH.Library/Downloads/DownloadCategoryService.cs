// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library.Search;

namespace StreamZH.Library.Downloads;

/// <summary>
/// A download category like SABnzbd's: where finished downloads go, their priority and how much post-processing they get.
/// <see cref="Folder"/> "" = the name below the completed folder; relative = below the completed folder; absolute = anywhere.
/// </summary>
/// <param name="Priority">null = default (normal unless the download says otherwise); <see cref="DownloadCategoryService.PausedPriority"/> = added paused.</param>
public sealed record DownloadCategoryDefinition(int Id, string Name, string Folder, int? Priority, PostProcessLevel PostProcessing)
{
    /// <summary>"movies", "series" and "other" cannot be renamed or deleted: grabs and old downloads use them.</summary>
    public bool IsDefault => DownloadCategoryService.DefaultNames.Contains(Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stores, validates and resolves download categories (cached; categories rarely change).</summary>
public sealed partial class DownloadCategoryService(DownloadCategoryRepository repository, DownloadSettings settings, ILogger<DownloadCategoryService> logger)
{
    public const int PausedPriority = -2;
    public const int MaxNameLength = 64;
    public const string Movies = "movies";
    public const string Series = "series";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> DefaultNames = [Movies, Series, Other];

    private readonly Lock _lock = new();
    private IReadOnlyList<DownloadCategoryDefinition>? _all;

    public static string DefaultName(DownloadCategory category) => category switch
    {
        DownloadCategory.Movie => Movies,
        DownloadCategory.Series => Series,
        _ => Other,
    };

    /// <summary>The media kind of a category name: movies and series for the defaults, other for custom categories.</summary>
    public static DownloadCategory KindOf(string name) =>
        string.Equals(name, Movies, StringComparison.OrdinalIgnoreCase) ? DownloadCategory.Movie
        : string.Equals(name, Series, StringComparison.OrdinalIgnoreCase) ? DownloadCategory.Series
        : DownloadCategory.Other;

    public IReadOnlyList<DownloadCategoryDefinition> All()
    {
        lock (_lock)
        {
            return _all ??= [.. repository.All().Select(ToDefinition)];
        }
    }

    public DownloadCategoryDefinition? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All().FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public DownloadCategoryDefinition? Find(int id) => All().FirstOrDefault(c => c.Id == id);

    /// <summary>The category of a download: by name when given, else the default one of its kind. A default that was lost is rebuilt.</summary>
    public DownloadCategoryDefinition Resolve(DownloadCategory category, string? name) =>
        Find(name) ?? Find(DefaultName(category)) ?? new DownloadCategoryDefinition(0, DefaultName(category), string.Empty, null, PostProcessLevel.Full);

    /// <summary>
    /// The folder finished downloads of this category go to. A folder that is not allowed (inside the data folder outside
    /// downloads/, e.g. saved by an older version) is replaced by the default one below the completed folder (0.12, V-02).
    /// </summary>
    public string FolderOf(DownloadCategoryDefinition category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var fallback = Path.Combine(settings.CompleteFolder, SafeName(category.Name));
        var folder = category.Folder.Trim();
        if (folder.Length == 0)
        {
            return settings.FolderProblem(fallback) is null ? fallback : Path.Combine(settings.DefaultCompleteFolder, SafeName(category.Name));
        }

        var resolved = Path.IsPathFullyQualified(folder) ? folder : Path.GetFullPath(Path.Combine(settings.CompleteFolder, folder));
        if (settings.FolderProblem(resolved) is { } problem)
        {
            logger.LogWarning("Download category '{Name}': {Folder} is not used. {Problem}", category.Name, resolved, problem);
            return Path.Combine(settings.DefaultCompleteFolder, SafeName(category.Name));
        }

        return resolved;
    }

    // Category names are checked on save (letters, digits, - and _); a name from elsewhere must not leave the folder.
    private static string SafeName(string name) => name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 ? Other : name;

    /// <summary>0.12 (V-02): why downloads may not go to <paramref name="folder"/>, null when they may.</summary>
    public string? FolderProblem(string folder) => settings.FolderProblem(folder);

    /// <exception cref="ValidationFailedException">Invalid fields (name, folder, priority).</exception>
    public DownloadCategoryDefinition Create(DownloadCategoryDefinition category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var normalized = Normalize(category) with { Id = 0 };
        Validate(normalized, null);
        var id = repository.Add(ToRecord(normalized));
        logger.LogInformation("Download category '{Name}' added", normalized.Name);
        Invalidate();
        return Find(id)!;
    }

    /// <exception cref="LibraryException">Not found.</exception>
    /// <exception cref="ValidationFailedException">Invalid fields, or a default category was renamed.</exception>
    public DownloadCategoryDefinition Update(int id, DownloadCategoryDefinition category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var existing = Find(id) ?? throw new LibraryException("Download category not found.", LibraryError.NotFound);
        var normalized = Normalize(category) with { Id = id };
        if (existing.IsDefault && !string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["name"] = [$"\"{existing.Name}\" is used for grabs and cannot be renamed."] });
        }

        Validate(normalized, id);
        repository.Update(ToRecord(normalized with { Name = existing.IsDefault ? existing.Name : normalized.Name }));
        logger.LogInformation("Download category '{Name}' saved", normalized.Name);
        Invalidate();
        return Find(id)!;
    }

    /// <exception cref="LibraryException">Not found, or a default category.</exception>
    public void Delete(int id)
    {
        var existing = Find(id) ?? throw new LibraryException("Download category not found.", LibraryError.NotFound);
        if (existing.IsDefault)
        {
            throw new LibraryException($"\"{existing.Name}\" is used for grabs and cannot be deleted.", LibraryError.Invalid);
        }

        repository.Delete(id);
        logger.LogInformation("Download category '{Name}' deleted", existing.Name);
        Invalidate();
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _all = null;
        }
    }

    private static DownloadCategoryDefinition Normalize(DownloadCategoryDefinition category) =>
        category with { Name = category.Name?.Trim() ?? string.Empty, Folder = category.Folder?.Trim() ?? string.Empty };

    private void Validate(DownloadCategoryDefinition category, int? id)
    {
        var errors = new Dictionary<string, string[]>();
        if (category.Name.Length is 0 or > MaxNameLength || !NameRegex().IsMatch(category.Name))
        {
            errors["name"] = [$"Use 1 to {MaxNameLength} letters, digits, spaces, dots, dashes or underscores."];
        }
        else if (All().Any(c => c.Id != id && string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)))
        {
            errors["name"] = ["A category with this name exists."];
        }

        var folder = category.Folder;
        if (folder.Length > 0)
        {
            if (Path.IsPathFullyQualified(folder))
            {
                if (settings.FolderProblem(folder) is { } dataProblem)
                {
                    errors["folder"] = [dataProblem];
                }
                else if (DownloadSettings.CheckWritable(folder) is { } problem)
                {
                    errors["folder"] = [problem];
                }
            }
            else if (Path.IsPathRooted(folder) || folder.Split('/', '\\').Any(part => part == ".."))
            {
                errors["folder"] = ["Use a folder name below the completed folder (no \"..\") or a full path."];
            }
        }

        if (category.Priority is < PausedPriority or > DownloadEngine.MaxPriority)
        {
            errors["priority"] = ["Use default, paused, low, normal, high or force."];
        }

        if (!Enum.IsDefined(category.PostProcessing))
        {
            errors["postProcessing"] = ["Use none, repair, unpack or full."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static DownloadCategoryDefinition ToDefinition(DownloadCategoryRecord r) =>
        new(r.Id, r.Name, r.Folder, r.Priority, Enum.IsDefined((PostProcessLevel)r.PostProcessing) ? (PostProcessLevel)r.PostProcessing : PostProcessLevel.Full);

    private static DownloadCategoryRecord ToRecord(DownloadCategoryDefinition c) => new(c.Id, c.Name, c.Folder, c.Priority, (int)c.PostProcessing);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")]
    private static partial Regex NameRegex();
}
