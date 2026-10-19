// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Host.Backups;
using StreamZH.Host.General;
using StreamZH.Host.Hubs;
using StreamZH.Host.Jobs;
using StreamZH.Host.Logging;
using StreamZH.Host.Service;
using StreamZH.Host.Settings;
using StreamZH.Host.Status;
using StreamZH.Host.Tray;
using StreamZH.Indexers;
using StreamZH.Library;
using StreamZH.Library.CustomFormats;
using StreamZH.Library.Downloads;
using StreamZH.Library.Rss;
using StreamZH.Library.SceneNumbering;
using StreamZH.Library.Import;
using StreamZH.Library.Organize;
using StreamZH.Library.Renamer;
using StreamZH.Library.Search;
using StreamZH.Library.Titles;
using StreamZH.Library.Wanted;
using StreamZH.Metadata.Tmdb;

// Windows service: connects to the service control manager once; restarts below keep that connection.
var service = ServiceSession.Connect();

// Restart (Settings → General, System → Status) runs everything from here again in this process; see AppLifecycle.
RestartState? restart = null;
start:

// Installed builds: content root = install folder, so wwwroot is found from any working directory.
// Development (dotnet run): the default content root plus static web assets from the project.
var installed = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = installed ? AppContext.BaseDirectory : null,
});

// --data <dir> on the command line, STREAMZH_DATA, /config in Docker, else the OS default.
var paths = AppPaths.Resolve(builder.Configuration["data"]);
paths.EnsureCreated();
var restored = new StartupRestore(PendingRestore.Apply(paths));

var database = new Database(paths.DatabaseFile);
database.Migrate();

var config = new ConfigRepository(database);
config.GetOrCreate(ConfigKeys.ApiKey, GeneralSettingsEndpoints.NewApiKey);
var fileLogger = builder.AddStreamZhLogging(paths, config);
var runningHost = builder.UseStreamZhHost(config, restart);
builder.UseStreamZhService(service);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddSingleton<ScheduledTaskRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduledTaskRunner>());
builder.Services.AddHostedService<HubNotifier>();

// Library
builder.Services.AddSingleton<RootFolderRepository>();
builder.Services.AddSingleton<MovieRepository>();
builder.Services.AddSingleton<SeriesRepository>();
builder.Services.AddSingleton<QualityProfileRepository>();
builder.Services.AddSingleton<CustomFormatRepository>();
builder.Services.AddSingleton<AlternativeTitleRepository>();
builder.Services.AddSingleton<RequestRepository>();
builder.Services.AddSingleton<InviteRepository>();
builder.Services.AddSingleton<DiscordLinkRepository>();
builder.Services.AddSingleton<QualityDefinitionRepository>();
builder.Services.AddSingleton<QualityProfileService>();
builder.Services.AddSingleton<QualityDefinitionService>();
builder.Services.AddSingleton<ITmdbSettings, TmdbSettings>();
builder.Services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);

    // Only for development and tests: point StreamZH at a TMDB stand-in.
    if (builder.Configuration["tmdb-url"] is { Length: > 0 } tmdbUrl)
    {
        client.BaseAddress = new Uri(tmdbUrl.EndsWith('/') ? tmdbUrl : tmdbUrl + "/");
    }
});
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<RootFolderService>();
builder.Services.AddScoped<LibraryImportService>();
builder.Services.AddIndexerManagement();
builder.Services.AddNewznabIndexers();
builder.Services.AddReleaseSearch();
builder.Services.AddRssSync();
builder.Services.AddOngoingStatus();
builder.Services.AddPostProcessing();
builder.Services.AddMediaInfo();
builder.Services.AddUsenet();
builder.Services.AddDownloadEngine();
builder.Services.AddConnect();
builder.Services.AddMediaManagement();
builder.Services.AddLibraryOrganize();
builder.Services.AddWanted();
builder.Services.AddRenamer();
builder.Services.AddImportLists();
builder.Services.AddTrash();
builder.Services.AddTrayAccess();
builder.Services.AddCustomFormats();
builder.Services.AddSceneNumbering(builder.Configuration["xem-url"]);
builder.Services.AddAlternativeTitles();
builder.Services.AddMigration();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(new StartupInfo(DateTimeOffset.UtcNow));
builder.Services.AddStreamZhAuth(paths);
builder.Services.AddGeneralSettings(restored);
builder.Services.AddSignalR();
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.UseStreamZhUrlBase();
app.UseExceptionHandler();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseBasicAuthenticationChallenge();
app.UseAuthorization();

app.MapOpenApi("/api/{documentName}/openapi.json");
app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapGeneralSettingsEndpoints();
app.MapUiSettingsEndpoints();
app.MapAutomationEndpoints();
app.MapSystemInfoEndpoints();
app.MapBackupEndpoints();
app.MapLogEndpoints();
app.MapToolSettingsEndpoints();
app.MapMediaInfoEndpoints();
app.MapDownloadSettingsEndpoints();
app.MapSystemTaskEndpoints();
app.MapLibraryEndpoints();
app.MapProfileEndpoints();
app.MapIndexerEndpoints();
app.MapFileSystemEndpoints();
app.MapParseEndpoints();
app.MapReleaseEndpoints();
app.MapIndexerOptionsEndpoints();
app.MapSearchEndpoints();
app.MapHistoryEndpoints();
app.MapStatusEndpoints();
app.MapUsenetEndpoints();
app.MapDownloadEndpoints();
app.MapConnectEndpoints();
app.MapMetadataEndpoints();
app.MapMediaManagementEndpoints();
app.MapOrganizeEndpoints();
app.MapWantedEndpoints();
app.MapCalendarEndpoints();
app.MapRenamerEndpoints();
app.MapImportListEndpoints();
app.MapTrashEndpoints();
app.MapTrayEndpoints();
app.MapCustomFormatEndpoints();
app.MapAlternativeTitleEndpoints();
app.MapMigrationEndpoints();
app.MapHub<EventsHub>("/hub").RequireAuthorization();

// Unknown API routes are real 404s; everything else is client-side routing in the React app.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapStreamZhIndex();

app.Logger.LogInformation("{App} {Version} started, data in {DataDirectory}", AppInfo.Name, AppInfo.Version, paths.DataDirectory);
if (restored.Outcome is { } restoreOutcome)
{
    app.Logger.Log(restoreOutcome.Success ? LogLevel.Warning : LogLevel.Error, "{Message}", restoreOutcome.Message);
}

restart = await app.RunUntilStoppedAsync(runningHost, fileLogger);
if (restart is not null && service?.StopRequested != true)
{
    goto start;
}

service?.Finish();

public sealed record StartupInfo(DateTimeOffset StartedAt);

public partial class Program;