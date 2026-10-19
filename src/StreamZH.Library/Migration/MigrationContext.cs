// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;

namespace StreamZH.Library.Migration;

/// <summary>
/// One section of a migration source, e.g. Radarr's custom formats or Sonarr's series. Sections are registered in DI
/// (<c>services.AddSingleton&lt;IMigrationSection, MySection&gt;()</c>); the <see cref="MigrationService"/> lists them per
/// <see cref="Source"/> ordered by <see cref="Order"/> (settings 100–499, library 500–799, history 800+).
/// </summary>
public interface IMigrationSection
{
    /// <summary>One of <see cref="MigrationSources"/>.</summary>
    string Source { get; }

    /// <summary>Stable key, unique per source, e.g. "customFormats".</summary>
    string Key { get; }

    string Label { get; }

    /// <summary>One sentence for the wizard, plain words.</summary>
    string Description { get; }

    int Order { get; }

    bool DefaultSelected { get; }

    /// <summary>Only listed with the Advanced switch on.</summary>
    bool Advanced { get; }

    /// <summary>
    /// Reads the old app and says what would happen to each item. Must not change anything. Register the paths the old
    /// app uses with <see cref="MigrationContext.Paths"/> so the wizard can check them.
    /// </summary>
    Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the section over. Skip items whose key is in <see cref="MigrationContext.IsExcluded"/>; record every item
    /// in <paramref name="run"/>; record id mappings (old id → new id) in <see cref="MigrationContext.Ids"/> for later
    /// sections. Must be safe to run twice (update instead of duplicate).
    /// </summary>
    Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IMigrationSection.PreviewAsync"/> returns.</summary>
public sealed record MigrationSectionPlan(IReadOnlyList<MigrationItem> Items, IReadOnlyList<string>? Warnings = null);

/// <summary>Everything a section needs while previewing or running.</summary>
public sealed class MigrationContext(
    string source,
    MigrationConnection connection,
    MigrationAppInfo app,
    MigrationRequest request,
    PathMapper paths,
    IServiceProvider services)
{
    public string Source { get; } = source;

    public MigrationConnection Connection { get; } = connection;

    public MigrationAppInfo App { get; } = app;

    public MigrationRequest Request { get; } = request;

    /// <summary>Maps the old app's paths to StreamZH's and collects them for the path check.</summary>
    public PathMapper Paths { get; } = paths;

    /// <summary>The host's services (repositories, library service, …), for sections that resolve them per run.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>Old id → new id per kind ("qualityProfile", "customFormat", "tag", "rootFolder", "movie", "series", …).</summary>
    public MigrationIdMap Ids { get; } = new();

    public bool IsExcluded(string itemKey) => Request.Exclude?.Contains(itemKey, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>A secret the user typed in the wizard for this item (e.g. a server password), or null.</summary>
    public string? Secret(string itemKey) => Request.Secrets?.GetValueOrDefault(itemKey) is { Length: > 0 } secret ? secret : null;

    /// <summary>A section option from the wizard, e.g. Option("movies.monitor", "asIs").</summary>
    public string Option(string key, string fallback) =>
        Request.Options?.GetValueOrDefault(key) is { Length: > 0 } value ? value : fallback;

    public bool OptionOn(string key, bool fallback) =>
        Request.Options?.GetValueOrDefault(key) is { Length: > 0 } value ? value is "true" or "1" or "on" : fallback;
}

public sealed class MigrationIdMap
{
    private readonly ConcurrentDictionary<(string Kind, string OldId), int> _map = new();

    public void Set(string kind, object oldId, int newId) => _map[(kind, Key(oldId))] = newId;

    public int? Get(string kind, object oldId) => _map.TryGetValue((kind, Key(oldId)), out var id) ? id : null;

    private static string Key(object id) => Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>Collects counts and messages of one section in a run and reports progress.</summary>
public sealed class MigrationSectionRun(string key, string label, Action<double, string?> progress)
{
    private readonly List<MigrationMessage> _messages = [];
    private int _created;
    private int _updated;
    private int _linked;
    private int _skipped;
    private int _failed;

    public string Key { get; } = key;

    public string Label { get; } = label;

    public void Created(string item) => Interlocked.Increment(ref _created);

    public void Updated(string item) => Interlocked.Increment(ref _updated);

    public void Linked(string item) => Interlocked.Increment(ref _linked);

    public void Skipped(string item, string reason)
    {
        Interlocked.Increment(ref _skipped);
        Add(MigrationMessageLevel.Info, reason, item);
    }

    public void Failed(string item, string error)
    {
        Interlocked.Increment(ref _failed);
        Add(MigrationMessageLevel.Error, error, item);
    }

    public void Info(string text, string? item = null) => Add(MigrationMessageLevel.Info, text, item);

    public void Warn(string text, string? item = null) => Add(MigrationMessageLevel.Warn, text, item);

    /// <summary>Progress inside this section, 0..1, with an optional step text ("Movie 12 of 340").</summary>
    public void Progress(int done, int total, string? step = null) => progress(total <= 0 ? 1 : Math.Clamp((double)done / total, 0, 1), step);

    public MigrationSectionReport Report()
    {
        lock (_messages)
        {
            return new MigrationSectionReport(Key, Label, _created, _updated, _linked, _skipped, _failed, [.. _messages]);
        }
    }

    private void Add(MigrationMessageLevel level, string text, string? item)
    {
        lock (_messages)
        {
            // A big library can produce thousands of skips; keep the report readable.
            if (_messages.Count < 2000)
            {
                _messages.Add(new MigrationMessage(level, text, item));
            }
        }
    }
}
