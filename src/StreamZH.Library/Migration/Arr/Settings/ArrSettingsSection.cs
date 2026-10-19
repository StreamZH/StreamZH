// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>One item of a settings section: what the preview shows and what the run does with it.</summary>
/// <param name="Item">The preview item; its <see cref="MigrationItem.Action"/> is counted when <paramref name="Apply"/> succeeds.</param>
/// <param name="Apply">Changes StreamZH (null for skipped items and for items that only link an id).</param>
public sealed record PlannedItem(MigrationItem Item, Action<MigrationSectionRun>? Apply = null);

/// <summary>What a settings section found: the items, section warnings and an optional last step (e.g. saving all values at once).</summary>
public sealed class SettingsPlan
{
    public List<PlannedItem> Items { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>Runs once after all items that were not left out.</summary>
    public Action<MigrationSectionRun>? Commit { get; set; }

    public void Add(MigrationItem item, Action<MigrationSectionRun>? apply = null) => Items.Add(new PlannedItem(item, apply));

    public void Skip(string key, string label, string reason, string? detail = null, string? warning = null) =>
        Items.Add(new PlannedItem(new MigrationItem(key, label, MigrationAction.Skip, detail, reason, warning)));
}

/// <summary>The old app answered 404 for an endpoint: this version does not have it.</summary>
public sealed class ArrEndpointMissingException(string message) : Exception(message);

/// <summary>
/// Reads the old app for one section (GET only) and gives the section the StreamZH services of one DI scope.
/// <see cref="Major"/> is the old app's major version (Radarr 4/5, Sonarr 3/4), from /system/status.
/// </summary>
public sealed class ArrReader : IDisposable
{
    private readonly IServiceScope _scope;
    private readonly ArrApiClient _client;

    public ArrReader(MigrationContext context, bool running = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Running = running;
        _scope = context.Services.CreateScope();
        _client = _scope.ServiceProvider.GetRequiredService<ArrApiClient>();
        Major = MajorVersion(context.Source, context.App.Version);
    }

    public MigrationContext Context { get; }

    /// <summary>True in a run, false in the preview (earlier sections have not created anything yet).</summary>
    public bool Running { get; }

    public string Source => Context.Source;

    public bool IsRadarr => Source == MigrationSources.Radarr;

    public MediaType MediaType => IsRadarr ? MediaType.Movie : MediaType.Series;

    /// <summary>"Radarr" or "Sonarr".</summary>
    public string AppName => ArrApiClient.Name(Source);

    public int Major { get; }

    /// <summary>Sonarr v3: language profiles, preferred words, no custom formats.</summary>
    public bool IsSonarrV3 => Source == MigrationSources.Sonarr && Major <= 3;

    public T Service<T>()
        where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public T? OptionalService<T>() => _scope.ServiceProvider.GetService<T>();

    /// <summary>
    /// GET {api}{path} as JSON (a copy that outlives the response). A 404 means this version of the app does not have the
    /// endpoint (the connection itself was checked before): <see cref="ArrEndpointMissingException"/>, which the wizard shows
    /// as a warning of this part instead of an error at the address field.
    /// </summary>
    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await _client.GetJsonAsync(Source, Context.Connection, path, cancellationToken);
            return document.RootElement.Clone();
        }
        catch (ValidationFailedException ex) when (ex.Errors.TryGetValue("url", out var messages) && messages.Any(m => m.Contains(" answered 404 for ", StringComparison.Ordinal)))
        {
            throw new ArrEndpointMissingException($"{AppName} {Context.App.Version} does not have {ArrApiClient.ApiPath(Source)}{path}.");
        }
    }

    /// <summary>A list endpoint; anything that is not an array counts as empty.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetListAsync(string path, CancellationToken cancellationToken)
    {
        var root = await GetAsync(path, cancellationToken);
        return root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : [];
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>"5.26.2.10099" → 5. Unknown versions count as the current ones (Radarr 5, Sonarr 4).</summary>
    public static int MajorVersion(string source, string? version)
    {
        var first = (version ?? string.Empty).Split('.')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > 0
            ? major
            : source == MigrationSources.Sonarr ? 4 : 5;
    }
}

/// <summary>
/// Base of the Radarr/Sonarr settings sections: one implementation per part, registered once per source. The preview and
/// the run build the same <see cref="SettingsPlan"/>, so item keys match; the run applies every item that was not left out
/// and counts it by its action. A failing item is reported and the next one still runs.
/// </summary>
public abstract class ArrSettingsSection(string source) : IMigrationSection
{
    public string Source { get; } = source;

    public abstract string Key { get; }

    public abstract string Label { get; }

    public abstract string Description { get; }

    public abstract int Order { get; }

    public virtual bool DefaultSelected => true;

    public virtual bool Advanced => false;

    protected string AppName => ArrApiClient.Name(Source);

    protected bool IsRadarr => Source == MigrationSources.Radarr;

    protected abstract Task<SettingsPlan> PlanAsync(ArrReader reader, CancellationToken cancellationToken);

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        using var reader = new ArrReader(context);
        var plan = await PlanAsync(reader, cancellationToken);
        return new MigrationSectionPlan([.. plan.Items.Select(i => i.Item)], plan.Warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(run);
        using var reader = new ArrReader(context, running: true);
        var plan = await PlanAsync(reader, cancellationToken);
        foreach (var warning in plan.Warnings)
        {
            run.Warn(warning);
        }

        for (var i = 0; i < plan.Items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, apply) = plan.Items[i];
            run.Progress(i, plan.Items.Count, item.Label);
            if (context.IsExcluded(item.Key))
            {
                run.Skipped(item.Label, "Left out in the wizard.");
                continue;
            }

            if (item.Action == MigrationAction.Skip)
            {
                run.Skipped(item.Label, item.Reason ?? "Not taken over.");
                if (item.Warning is { } skipWarning)
                {
                    run.Warn(skipWarning, item.Label);
                }

                continue;
            }

            try
            {
                apply?.Invoke(run);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Failed(item.Label, Message(ex));
                continue;
            }

            switch (item.Action)
            {
                case MigrationAction.Create: run.Created(item.Label); break;
                case MigrationAction.Update: run.Updated(item.Label); break;
                default: run.Linked(item.Label); break;
            }

            if (item.Warning is { } warning)
            {
                run.Warn(warning, item.Label);
            }
        }

        if (plan.Commit is { } commit)
        {
            try
            {
                commit(run);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Failed(Label, Message(ex));
            }
        }

        run.Progress(plan.Items.Count, plan.Items.Count);
    }

    /// <summary>Plain text of an error, with the field messages of a validation error.</summary>
    public static string Message(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ValidationFailedException validation
            ? string.Join(" ", validation.Errors.Values.SelectMany(v => v))
            : exception.Message;
    }

    /// <summary>"customFormats:12": the item key of an old id, stable between preview and run.</summary>
    protected string ItemKey(object id) => $"{Key}:{Convert.ToString(id, CultureInfo.InvariantCulture)}";
}
