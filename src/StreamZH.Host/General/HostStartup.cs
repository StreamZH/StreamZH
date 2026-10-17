// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using StreamZH.Data;

namespace StreamZH.Host.General;

/// <summary>
/// What this run of StreamZH listens on. <see cref="Override"/>: addresses from --urls, ASPNETCORE_URLS or a Kestrel
/// section in appsettings, which win over the settings page. <see cref="Problem"/>: why the settings were not used
/// completely (certificate, address in use). <see cref="Previous"/>: the values of the run before an in-process restart;
/// <see cref="Fallback"/>: this run uses them because the new ones could not be bound.
/// </summary>
public sealed record RunningHost(HostValues Values, string? Override, IReadOnlyList<string> Addresses, string? Problem, int Run, HostValues? Previous = null, bool Fallback = false)
{
    public bool IsRestart => Run > 1;
}

/// <summary>Carried from one run to the next when StreamZH restarts in-process (see <see cref="AppLifecycle"/>).</summary>
/// <param name="LastGood">The host values the previous run listened on.</param>
/// <param name="UseLastGood">True after the new values could not be bound: start once more with <paramref name="LastGood"/>.</param>
public sealed record RestartState(int Run, HostValues LastGood, bool UseLastGood, string? BindError);

public static class HostStartup
{
    private static readonly string[] BackendPrefixes = ["/api", "/hub", "/health"];

    /// <summary>
    /// Applies the host settings before Kestrel is built: bind address, port and HTTPS, unless --urls, ASPNETCORE_URLS or
    /// a Kestrel section in appsettings sets the addresses (those win, like in Radarr). The URL base always applies.
    /// </summary>
    public static RunningHost UseStreamZhHost(this WebApplicationBuilder builder, ConfigRepository config, RestartState? previous)
    {
        var values = previous is { UseLastGood: true } ? previous.LastGood : new HostSettings(new SettingsCache(config)).Current;
        var explicitUrls = ExplicitUrls(builder.Configuration);
        var problem = previous?.BindError;
        var addresses = new List<string>();

        if (explicitUrls is null)
        {
            X509Certificate2? certificate = null;
            if (values.EnableSsl)
            {
                certificate = HostSettings.LoadCertificate(values.SslCertPath, values.SslCertPassword, out var certificateProblem);
                if (certificate is null)
                {
                    problem = Join(problem, $"HTTPS is off: {certificateProblem}");
                }
            }

            // The ASP.NET images set HTTP_PORTS=8080; the settings decide here, without a warning about overridden addresses.
            builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, string.Empty);
            builder.WebHost.UseSetting(WebHostDefaults.HttpsPortsKey, string.Empty);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                Listen(kestrel, values.BindAddress, values.Port, null);
                if (certificate is not null)
                {
                    Listen(kestrel, values.BindAddress, values.SslPort, certificate);
                }
            });

            addresses.Add(Url("http", values.BindAddress, values.Port));
            if (certificate is not null)
            {
                addresses.Add(Url("https", values.BindAddress, values.SslPort));
            }
        }
        else
        {
            addresses.AddRange(explicitUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var running = new RunningHost(values, explicitUrls, addresses, problem, (previous?.Run ?? 0) + 1, previous?.LastGood, previous?.UseLastGood == true);
        builder.Services.AddSingleton(running);
        return running;
    }

    /// <summary>Addresses set outside the settings page, or null.</summary>
    public static string? ExplicitUrls(IConfiguration configuration)
    {
        if (configuration["urls"] is { Length: > 0 } urls)
        {
            return urls;
        }

        return configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() ? "Kestrel endpoints in appsettings.json" : null;
    }

    public static string Url(string scheme, string bindAddress, int port)
    {
        var host = bindAddress == HostSettings.AllAddresses
            ? "*"
            : IPAddress.TryParse(bindAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : bindAddress;
        return string.Create(CultureInfo.InvariantCulture, $"{scheme}://{host}:{port}");
    }

    private static void Listen(KestrelServerOptions kestrel, string bindAddress, int port, X509Certificate2? certificate)
    {
        Action<ListenOptions> configure = options =>
        {
            if (certificate is not null)
            {
                options.UseHttps(certificate);
            }
        };

        if (bindAddress == HostSettings.AllAddresses || !IPAddress.TryParse(bindAddress, out var address))
        {
            kestrel.ListenAnyIP(port, configure);
        }
        else
        {
            kestrel.Listen(address, port, configure);
        }
    }

    /// <summary>
    /// The URL base (Settings → General, for reverse proxies like "https://example.com/streamzh"): requests under it get
    /// it as PathBase, so API, SignalR and the web app work below it; pages opened without it are redirected there.
    /// Calls UseRouting itself, routing must come after the path base.
    /// </summary>
    public static WebApplication UseStreamZhUrlBase(this WebApplication app)
    {
        var urlBase = app.Services.GetRequiredService<RunningHost>().Values.UrlBase;
        if (urlBase.Length > 0)
        {
            app.UsePathBase(urlBase);
            app.Use((context, next) =>
            {
                var path = context.Request.Path.Value ?? "/";
                if (context.Request.PathBase.HasValue
                    || !HttpMethods.IsGet(context.Request.Method)
                    || Path.HasExtension(path)
                    || BackendPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return next(context);
                }

                context.Response.Redirect(urlBase + (path == "/" ? "/" : path) + context.Request.QueryString);
                return Task.CompletedTask;
            });
        }

        app.UseRouting();
        return app;
    }

    /// <summary>Serves the web app's index.html (with the URL base as &lt;base href&gt;) for "/", "/index.html" and every client route.</summary>
    public static WebApplication MapStreamZhIndex(this WebApplication app)
    {
        app.MapGet("/index.html", IndexPage.Serve).ExcludeFromDescription();
        app.MapFallback(IndexPage.Serve);
        return app;
    }

    /// <summary>
    /// Runs the app until it stops. Returns the state for the next run when a restart was asked for, else null. A restart
    /// whose new address cannot be bound (port in use, address not on this computer) starts once more with the old one.
    /// </summary>
    public static async Task<RestartState?> RunUntilStoppedAsync(this WebApplication app, RunningHost running, IDisposable logs)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(logs);
        RestartState? next = null;
        try
        {
            await app.RunAsync();
            if (app.Services.GetRequiredService<AppLifecycle>().RestartRequested)
            {
                next = new RestartState(running.Run, running.Values, false, null);
            }
        }
        catch (IOException ex) when (running is { IsRestart: true, Fallback: false, Override: null })
        {
            var addresses = string.Join(", ", running.Addresses);
            app.Logger.LogCritical(ex, "Could not listen on {Addresses}: {Message} Starting again with the previous address", addresses, ex.Message);
            var lastGood = running.Previous ?? new HostValues(
                HostSettings.AllAddresses, HostSettings.DefaultPort, running.Values.UrlBase, false, HostSettings.DefaultSslPort, null, null);
            next = new RestartState(running.Run, lastGood, true, $"Could not listen on {addresses}: {ex.Message}");
        }
        finally
        {
            await app.DisposeAsync();
            logs.Dispose();
            SqliteConnection.ClearAllPools();
        }

        return next;
    }

    private static string Join(string? first, string second) => string.IsNullOrEmpty(first) ? second : $"{first} {second}";
}
