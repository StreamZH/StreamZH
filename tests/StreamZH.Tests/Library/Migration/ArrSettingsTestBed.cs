// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Import;
using StreamZH.Library.Migration;
using StreamZH.Library.Migration.Arr.Settings;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Library.Migration;

/// <summary>
/// Answers /api/v3/… like Radarr or Sonarr from the JSON files in Fixtures/&lt;app&gt;/ ("/config/naming" → config_naming.json).
/// Unknown paths get 404. Records every request so tests can check that only GET was used.
/// </summary>
public sealed class ArrFixtureHandler : HttpMessageHandler
{
    public const string ApiKey = "0123456789abcdef0123456789abcdef";

    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Library", "Migration", "Fixtures");

    /// <summary>The fixture folder: radarr5, sonarr4 or sonarr3.</summary>
    public string App { get; set; } = "radarr5";

    /// <summary>Answers that replace a fixture file, by path ("/qualityprofile").</summary>
    public ConcurrentDictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<(HttpMethod Method, string Path)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method, path));
        if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.FirstOrDefault() != ApiKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        const string prefix = "/api/v3";
        if (request.Method != HttpMethod.Get || !path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var relative = path[prefix.Length..];
        string? body = Overrides.TryGetValue(relative, out var overridden) ? overridden : null;
        if (body is null)
        {
            var file = Path.Combine(FixtureDirectory, App, relative.Trim('/').Replace('/', '_') + ".json");
            body = File.Exists(file) ? File.ReadAllText(file) : null;
        }

        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>A migrated database, StreamZH's settings services and the Radarr/Sonarr settings sections against the fixture handler.</summary>
public sealed class ArrSettingsTestBed : IDisposable
{
    public ArrSettingsTestBed()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(MoviesFolder);
        Directory.CreateDirectory(TvFolder);
        var paths = new AppPaths(DataDirectory);
        Database = new Database(paths.DatabaseFile);
        Database.Migrate();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(paths);
        services.AddSingleton(Database);
        services.AddSingleton<ConfigRepository>();
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<RootFolderRepository>();
        services.AddSingleton<MovieRepository>();
        services.AddSingleton<SeriesRepository>();
        services.AddSingleton<QualityProfileRepository>();
        services.AddSingleton<CustomFormatRepository>();
        services.AddSingleton<QualityDefinitionRepository>();
        services.AddSingleton<ReleaseProfileRepository>();
        services.AddSingleton<IndexerRepository>();
        services.AddSingleton<ImportListRepository>();
        services.AddSingleton<QualityProfileService>();
        services.AddSingleton<QualityDefinitionService>();
        services.AddScoped<RootFolderService>();
        services.AddSingleton<MediaManagementSettings>();
        services.AddCustomFormats();
        services.AddConnectNotifications();
        services.AddMigrationCore();
        services.AddArrSettingsMigration();
        services.AddHttpClient<ArrApiClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-arrsettings-{Guid.NewGuid():N}");

    public string MoviesFolder => Path.Combine(DataDirectory, "media", "movies");

    public string TvFolder => Path.Combine(DataDirectory, "media", "tv");

    public Database Database { get; }

    public ServiceProvider Services { get; }

    public ArrFixtureHandler Handler { get; } = new();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A request with the old app's paths (/movies, /tv) mapped to the test folders.</summary>
    public MigrationRequest Request(string source) => new(
        source == MigrationSources.Radarr ? "http://radarr:7878" : "http://sonarr:8989",
        ArrFixtureHandler.ApiKey,
        [new PathMapping("/movies", MoviesFolder), new PathMapping("/tv", TvFolder)]);

    /// <summary>A context like the migration service builds one; the app and version come from the fixture's system_status.json.</summary>
    public MigrationContext Context(string source, string app, MigrationRequest? request = null)
    {
        Handler.App = app;
        request ??= Request(source);
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(ArrFixtureHandler.FixtureDirectory, app, "system_status.json")));
        var info = new MigrationAppInfo(status.RootElement.GetProperty("appName").GetString()!, status.RootElement.GetProperty("version").GetString()!);
        return new MigrationContext(source, ArrApiClient.Validate(source, request.Url, request.ApiKey), info, request, new PathMapper(request.PathMappings), Services);
    }

    public IMigrationSection Section(string source, string key) =>
        Services.GetServices<IMigrationSection>().Single(s => s.Source == source && s.Key == key);

    public Task<MigrationSectionPlan> PreviewAsync(string source, string app, string key, MigrationRequest? request = null) =>
        Section(source, key).PreviewAsync(Context(source, app, request), CancellationToken.None);

    /// <summary>Runs the sections in order in one context (like one migration run) and returns their reports.</summary>
    public async Task<IReadOnlyList<MigrationSectionReport>> RunAsync(string source, string app, MigrationRequest? request, params string[] keys)
    {
        var context = Context(source, app, request);
        var reports = new List<MigrationSectionReport>();
        foreach (var key in keys)
        {
            var section = Section(source, key);
            var run = new MigrationSectionRun(section.Key, section.Label, (_, _) => { });
            await section.RunAsync(context, run, CancellationToken.None);
            reports.Add(run.Report());
        }

        return reports;
    }

    public async Task<MigrationSectionReport> RunAsync(string source, string app, string key, MigrationRequest? request = null) =>
        (await RunAsync(source, app, request, key))[0];

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
