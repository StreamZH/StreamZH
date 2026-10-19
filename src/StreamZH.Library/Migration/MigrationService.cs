// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Events;
using StreamZH.Core.Net;
using StreamZH.Data;

namespace StreamZH.Library.Migration;

/// <summary>A migration run changed (progress, section done, finished). The wizard re-reads the run.</summary>
public sealed record MigrationChanged(int RunId) : IAppEvent;

/// <summary>A source and its sections, for the wizard's first step.</summary>
public sealed record MigrationSourceInfo(string Key, string Label, IReadOnlyList<MigrationSectionInfo> Sections);

public sealed record MigrationSectionInfo(string Key, string Label, string Description, int Order, bool DefaultSelected, bool Advanced);

/// <summary>
/// The migration wizard's engine: tests the connection, builds the preview from all sections of a source and runs the
/// selected sections in the background (one run at a time), keeping progress in memory and the report in the database.
/// Read-only towards the old app; API keys and typed-in secrets live only for the request or run.
/// </summary>
public sealed class MigrationService(
    IEnumerable<IMigrationSection> sections,
    IEnumerable<IMigrationProbe> probes,
    MigrationRunRepository runs,
    IServiceProvider services,
    IEventBus events,
    ILogger<MigrationService> logger)
{
    private readonly ConcurrentDictionary<int, LiveRun> _live = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>
    /// The path mappings of the request. 0.12 (SV2 N-03, N-06): the local side must be inside a storage location (checked
    /// before the wizard looks at the folder, so it never opens a network path the owner did not allow).
    /// </summary>
    /// <exception cref="ValidationFailedException">
    /// A local folder is not a valid path, or is outside the storage locations (code <c>outside-storage</c> with all such
    /// folders in <see cref="ValidationFailedException.Paths"/>, so the wizard can offer the owner to allow them at once).
    /// </exception>
    private PathMapper Mapper(MigrationRequest request)
    {
        if (services.GetService(typeof(StreamZH.Core.AppPaths)) is not StreamZH.Core.AppPaths paths)
        {
            return new PathMapper(request.PathMappings);
        }

        var outside = new List<(string Folder, string Problem)>();
        foreach (var mapping in request.PathMappings ?? [])
        {
            if (string.IsNullOrWhiteSpace(mapping.Local))
            {
                continue;
            }

            if (StreamZH.Core.PathIdentity.SyntaxProblem(mapping.Local, OperatingSystem.IsWindows()) is { } syntax)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["pathMappings"] = [syntax] });
            }

            if (paths.StorageProblem(mapping.Local) is { } problem && !outside.Any(o => o.Folder == mapping.Local.Trim()))
            {
                outside.Add((mapping.Local.Trim(), problem));
            }
        }

        if (outside.Count > 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["pathMappings"] = [.. outside.Select(o => o.Problem)] })
            {
                Code = StreamZH.Core.AppPaths.OutsideStorageCode,
                Paths = [.. outside.Select(o => o.Folder)],
            };
        }

        // Folders of the old app without a mapping are checked against the storage locations before StreamZH looks at
        // them (a network path the old app reports is never opened unless the owner allowed it).
        return new PathMapper(request.PathMappings, storageProblem: folder => paths.StorageProblem(folder));
    }

    public IReadOnlyList<MigrationSourceInfo> Sources() =>
    [
        .. MigrationSources.All.Select(source => new MigrationSourceInfo(
            source,
            ArrApiClient.Name(source),
            [.. For(source).Select(s => new MigrationSectionInfo(s.Key, s.Label, s.Description, s.Order, s.DefaultSelected, s.Advanced))])),
    ];

    public async Task<MigrationTestResult> TestAsync(string source, MigrationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var (_, app) = await ProbeAsync(source, request, cancellationToken);
            return new MigrationTestResult(true, app, null);
        }
        catch (ValidationFailedException ex)
        {
            return new MigrationTestResult(false, null, string.Join(" ", ex.Errors.Values.SelectMany(v => v)));
        }
    }

    public async Task<MigrationPreview> PreviewAsync(string source, MigrationRequest request, CancellationToken cancellationToken)
    {
        var (connection, app) = await ProbeAsync(source, request, cancellationToken);
        var context = new MigrationContext(source, connection, app, request, Mapper(request), services);
        var previews = new List<MigrationSectionPreview>();
        var warnings = new List<string>();
        foreach (var section in For(source))
        {
            try
            {
                var plan = await section.PreviewAsync(context, cancellationToken);
                previews.Add(new MigrationSectionPreview(
                    section.Key, section.Label, section.Description, section.Order, section.DefaultSelected, section.Advanced, plan.Items, plan.Warnings ?? []));
            }
            catch (ValidationFailedException ex) when (!MissingEndpoint(ex))
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Also a 404 for one endpoint (this version of the app lacks it): the probe reached the app, so only this part is affected.
                var message = ex is ValidationFailedException validation ? string.Join(" ", validation.Errors.Values.SelectMany(v => v)) : ex.Message;
                if (ex is ValidationFailedException)
                {
                    logger.LogInformation("Migration preview of {Source}/{Section}: {Message}", source, section.Key, message);
                }
                else
                {
                    logger.LogWarning(ex, "Migration preview of {Source}/{Section} failed", source, section.Key);
                }

                previews.Add(new MigrationSectionPreview(
                    section.Key, section.Label, section.Description, section.Order, false, section.Advanced, [], [$"Could not read this part: {message}"]));
            }
        }

        var paths = context.Paths.Checks();
        if (paths.Any(p => p.OutsideStorage))
        {
            warnings.Add("Some folders of " + app.AppName + " are outside the storage locations. The owner allows them in the Folders step (or under Settings → General → Storage).");
        }

        if (paths.Any(p => !p.Exists && !p.OutsideStorage))
        {
            warnings.Add("StreamZH cannot see some folders of " + app.AppName + ". Add a path mapping (e.g. /tv → /data/media/tv) before you take over the library.");
        }

        return new MigrationPreview(source, app, previews, paths, warnings);
    }

    /// <summary>Starts a run in the background and returns it right away (status queued/running).</summary>
    public async Task<MigrationRun> StartAsync(string source, MigrationRequest request, string? userName, CancellationToken cancellationToken)
    {
        // Validate the path mappings and the connection before anything is stored, so a wrong key shows at the field.
        _ = Mapper(request);
        var (connection, app) = await ProbeAsync(source, request, cancellationToken);
        var selected = Selected(source, request);
        if (selected.Count == 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["sections"] = ["Pick at least one part to take over."] });
        }

        var started = DateTime.UtcNow;
        var id = runs.Add(source, Status(MigrationRunStatus.Queued), request.DryRun, started, userName);
        var live = new LiveRun(id, source, request.DryRun, started, app);
        _live[id] = live;
        runs.Update(id, Status(MigrationRunStatus.Queued), null, null, JsonSerializer.Serialize(app, ArrApiClient.Json), null);
        _ = Task.Run(() => ExecuteAsync(live, connection, app, request, selected), CancellationToken.None);
        return live.Snapshot();
    }

    public IReadOnlyList<MigrationRun> Runs() => [.. runs.All().Select(r => _live.TryGetValue(r.Id, out var live) ? live.Snapshot() : FromRow(r))];

    public MigrationRun? Find(int id) => _live.TryGetValue(id, out var live) ? live.Snapshot() : runs.Find(id) is { } row ? FromRow(row) : null;

    public bool Cancel(int id)
    {
        if (!_live.TryGetValue(id, out var live))
        {
            return false;
        }

        live.Cancel.Cancel();
        return true;
    }

    private async Task ExecuteAsync(LiveRun live, MigrationConnection connection, MigrationAppInfo app, MigrationRequest request, IReadOnlyList<IMigrationSection> selected)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            var token = live.Cancel.Token;
            live.Status = MigrationRunStatus.Running;
            await PublishAsync(live);
            var context = new MigrationContext(live.Source, connection, app, request, Mapper(request), services);
            for (var i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var section = selected[i];
                var index = i;
                var run = new MigrationSectionRun(section.Key, section.Label, (p, step) =>
                {
                    live.Progress = (index + p) / selected.Count;
                    live.Step = step is null ? section.Label : $"{section.Label}: {step}";
                    live.Throttled(() => _ = PublishAsync(live));
                });
                live.Step = section.Label;
                live.Current = run;
                await PublishAsync(live);
                try
                {
                    if (request.DryRun)
                    {
                        await DryRunAsync(section, context, run, token);
                    }
                    else
                    {
                        await section.RunAsync(context, run, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    live.AddDone(run.Report());
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Migration {Source}/{Section} failed", live.Source, section.Key);
                    run.Failed(section.Label, ex is ValidationFailedException v ? string.Join(" ", v.Errors.Values.SelectMany(e => e)) : ex.Message);
                }

                live.Current = null;
                live.AddDone(run.Report());
                live.Progress = (double)(i + 1) / selected.Count;
                Persist(live, MigrationRunStatus.Running, null);
                await PublishAsync(live);
            }

            live.Status = MigrationRunStatus.Done;
            live.Step = null;
            live.Finished = DateTime.UtcNow;
            Persist(live, MigrationRunStatus.Done, null);
            logger.LogInformation("Migration from {Source} finished: {Sections} parts", live.Source, live.DoneList().Count);
        }
        catch (OperationCanceledException)
        {
            live.Status = MigrationRunStatus.Cancelled;
            live.Finished = DateTime.UtcNow;
            Persist(live, MigrationRunStatus.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration from {Source} failed", live.Source);
            live.Status = MigrationRunStatus.Failed;
            live.Error = ex.Message;
            live.Finished = DateTime.UtcNow;
            Persist(live, MigrationRunStatus.Failed, ex.Message);
        }
        finally
        {
            _oneAtATime.Release();
            await PublishAsync(live);
            _live.TryRemove(live.Id, out _);
        }
    }

    /// <summary>A dry run reports what the preview says would happen, without changing anything.</summary>
    private static async Task DryRunAsync(IMigrationSection section, MigrationContext context, MigrationSectionRun run, CancellationToken token)
    {
        var plan = await section.PreviewAsync(context, token);
        foreach (var item in plan.Items)
        {
            if (context.IsExcluded(item.Key))
            {
                run.Skipped(item.Label, "Left out in the wizard.");
                continue;
            }

            switch (item.Action)
            {
                case MigrationAction.Create: run.Created(item.Label); break;
                case MigrationAction.Update: run.Updated(item.Label); break;
                case MigrationAction.Link: run.Linked(item.Label); break;
                default: run.Skipped(item.Label, item.Reason ?? "Not taken over."); break;
            }
        }

        foreach (var warning in plan.Warnings ?? [])
        {
            run.Warn(warning);
        }
    }

    private async Task<(MigrationConnection Connection, MigrationAppInfo App)> ProbeAsync(string source, MigrationRequest request, CancellationToken cancellationToken)
    {
        var probe = probes.FirstOrDefault(p => p.Handles(source))
            ?? throw new ValidationFailedException(new Dictionary<string, string[]> { ["source"] = [$"Unknown source '{source}'."] });
        return await probe.ProbeAsync(source, request.Url, request.ApiKey, cancellationToken);
    }

    private IEnumerable<IMigrationSection> For(string source) => sections.Where(s => s.Source == source).OrderBy(s => s.Order).ThenBy(s => s.Key, StringComparer.Ordinal);

    private List<IMigrationSection> Selected(string source, MigrationRequest request)
    {
        var all = For(source).ToList();
        if (request.Sections is null)
        {
            return [.. all.Where(s => s.DefaultSelected)];
        }

        var wanted = request.Sections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = wanted.Where(k => all.All(s => !s.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["sections"] = [$"Unknown part: {string.Join(", ", unknown)}."] });
        }

        return [.. all.Where(s => wanted.Contains(s.Key))];
    }

    private void Persist(LiveRun live, MigrationRunStatus status, string? error)
    {
        try
        {
            runs.Update(
                live.Id, Status(status), live.Finished, error, JsonSerializer.Serialize(live.App, ArrApiClient.Json),
                JsonSerializer.Serialize(new MigrationReport(live.DoneList()), ArrApiClient.Json));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not store migration run {Id}", live.Id);
        }
    }

    /// <summary>ArrApiClient's "answered 404 for /api/v3/…": the endpoint is missing, not the app.</summary>
    private static bool MissingEndpoint(ValidationFailedException ex) =>
        ex.Errors.TryGetValue("url", out var messages) && messages.Any(m => m.Contains(" answered 404 for ", StringComparison.Ordinal));

    private ValueTask PublishAsync(LiveRun live) => events.PublishAsync(new MigrationChanged(live.Id));

    private static string Status(MigrationRunStatus status) => JsonSerializer.Serialize(status).Trim('"');

    private static MigrationRun FromRow(MigrationRunRow row)
    {
        var status = Enum.TryParse<MigrationRunStatus>(row.Status, ignoreCase: true, out var s) ? s : MigrationRunStatus.Failed;

        // A run that was queued or running when StreamZH stopped did not finish.
        if (status is MigrationRunStatus.Queued or MigrationRunStatus.Running)
        {
            status = MigrationRunStatus.Failed;
        }

        return new MigrationRun(
            row.Id,
            row.Source,
            status,
            row.DryRun,
            row.Started,
            row.Finished,
            status == MigrationRunStatus.Done ? 1 : 0,
            null,
            row.Error ?? (s is MigrationRunStatus.Queued or MigrationRunStatus.Running ? "StreamZH stopped during this run." : null),
            Deserialize<MigrationAppInfo>(row.App),
            Deserialize<MigrationReport>(row.Report) ?? MigrationReport.Empty);
    }

    private static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, ArrApiClient.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class LiveRun(int id, string source, bool dryRun, DateTime started, MigrationAppInfo app)
    {
        private long _lastPublish;

        public int Id { get; } = id;

        public string Source { get; } = source;

        public MigrationAppInfo App { get; } = app;

        public CancellationTokenSource Cancel { get; } = new();

        private readonly List<MigrationSectionReport> _done = [];

        public void AddDone(MigrationSectionReport report)
        {
            lock (_done)
            {
                _done.Add(report);
            }
        }

        public List<MigrationSectionReport> DoneList()
        {
            lock (_done)
            {
                return [.. _done];
            }
        }

        public MigrationSectionRun? Current { get; set; }

        public MigrationRunStatus Status { get; set; } = MigrationRunStatus.Queued;

        public double Progress { get; set; }

        public string? Step { get; set; }

        public string? Error { get; set; }

        public DateTime? Finished { get; set; }

        public void Throttled(Action action)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastPublish);
            if (now - last >= 500 && Interlocked.CompareExchange(ref _lastPublish, now, last) == last)
            {
                action();
            }
        }

        public MigrationRun Snapshot()
        {
            var sections = DoneList();
            if (Current is { } current)
            {
                sections.Add(current.Report());
            }

            return new MigrationRun(Id, Source, Status, dryRun, started, Finished, Math.Round(Progress, 4), Step, Error, App, new MigrationReport(sections));
        }
    }
}

public static class MigrationServiceCollectionExtensions
{
    /// <summary>
    /// The migration engine, the Radarr/Sonarr/Prowlarr API client and probe. Sections and other probes (SABnzbd) are
    /// registered by their own Add… methods.
    /// </summary>
    public static IServiceCollection AddMigrationCore(this IServiceCollection services)
    {
        services.AddHttpClient<ArrApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.MaxResponseContentBufferSize = ResponseLimits.ArrMigration;
        });
        services.AddSingleton<IMigrationProbe, ArrMigrationProbe>();
        services.AddSingleton<MigrationRunRepository>();
        services.AddSingleton<MigrationService>();
        return services;
    }
}
