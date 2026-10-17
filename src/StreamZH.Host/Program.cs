// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Host.Api;
using StreamZH.Host.Hubs;

var builder = WebApplication.CreateBuilder(args);

// --data <dir> on the command line, STREAMZH_DATA, /config in Docker, else the OS default.
var paths = AppPaths.Resolve(builder.Configuration["data"]);
paths.EnsureCreated();

var database = new Database(paths.DatabaseFile);
database.Migrate();

var config = new ConfigRepository(database);
config.GetOrCreate("ApiKey", () => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton(new StartupInfo(DateTimeOffset.UtcNow));
builder.Services.AddSignalR();
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi("/api/{documentName}/openapi.json");
app.MapSystemEndpoints();
app.MapHub<EventsHub>("/hub");

// Client-side routing: unknown non-API paths serve the React app.
app.MapFallbackToFile("index.html");

app.Run();

public sealed record StartupInfo(DateTimeOffset StartedAt);

public partial class Program;