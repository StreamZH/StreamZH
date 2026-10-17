// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Jobs;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Auth;
using StreamZH.Host.Hubs;
using StreamZH.Host.Jobs;
using StreamZH.Host.Logging;
using StreamZH.Host.Settings;
using StreamZH.Indexers;
using StreamZH.Library;
using StreamZH.Metadata.Tmdb;

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

var database = new Database(paths.DatabaseFile);
database.Migrate();

var config = new ConfigRepository(database);
config.GetOrCreate(ConfigKeys.ApiKey, SettingsEndpoints.NewApiKey);

var logLevel = new LogLevelSwitch(Enum.TryParse<LogLevel>(config.Get(ConfigKeys.LogLevel), out var level) ? level : LogLevel.Information);
var fileLogger = new FileLoggerProvider(paths.LogDirectory, logLevel);
builder.Logging.AddProvider(fileLogger);

// The file log follows the level from the settings page; framework noise stays at Warning.
builder.Logging.AddFilter<FileLoggerProvider>(null, LogLevel.Trace);
builder.Logging.AddFilter<FileLoggerProvider>("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter<FileLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);

// Keys are stored unencrypted in the data folder by design (same as Sonarr/Radarr); no warning on every start.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.Logging.AddFilter<FileLoggerProvider>("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

// One line per outgoing HTTP request (TMDB, indexers) is noise; keep warnings and errors only.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter<FileLoggerProvider>("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(logLevel);
builder.Services.AddSingleton(fileLogger);
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService<HubNotifier>();

// Library
builder.Services.AddSingleton<RootFolderRepository>();
builder.Services.AddSingleton<MovieRepository>();
builder.Services.AddSingleton<SeriesRepository>();
builder.Services.AddSingleton<QualityProfileRepository>();
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
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(new StartupInfo(DateTimeOffset.UtcNow));
builder.Services.AddStreamZhAuth(paths);
builder.Services.AddSignalR();
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/api/{documentName}/openapi.json");
app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapSettingsEndpoints();
app.MapLibraryEndpoints();
app.MapProfileEndpoints();
app.MapIndexerEndpoints();
app.MapFileSystemEndpoints();
app.MapParseEndpoints();
app.MapHub<EventsHub>("/hub").RequireAuthorization();

// Unknown API routes are real 404s; everything else is client-side routing in the React app.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("{App} {Version} started, data in {DataDirectory}", AppInfo.Name, AppInfo.Version, paths.DataDirectory);

app.Run();

public sealed record StartupInfo(DateTimeOffset StartedAt);

public partial class Program;