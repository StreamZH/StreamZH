// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Host.General;
using StreamZH.Host.Service;

namespace StreamZH.Tests.Host;

/// <summary>Running under a service manager: "Runs as" detection and the session that outlives in-process restarts.</summary>
public sealed class ServiceHostingTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-service-{Guid.NewGuid():N}");
    private readonly ConfigRepository _config;

    public ServiceHostingTests()
    {
        Directory.CreateDirectory(_dir);
        var database = new Database(new AppPaths(_dir).DatabaseFile);
        database.Migrate();
        _config = new ConfigRepository(database);
        _config.Set(ConfigKeys.BindAddress, "127.0.0.1");
        _config.Set(ConfigKeys.Port, FreePort().ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("true", false, null, "docker")]
    [InlineData("true", false, "systemd", "docker")]
    [InlineData(null, true, null, "windowsService")]
    [InlineData(null, false, "systemd", "systemd")]
    [InlineData(null, false, "bash", "console")]
    [InlineData(null, false, "dotnet", "console")]
    [InlineData(null, false, null, "console")]
    [InlineData("false", false, null, "console")]
    public void Runs_as_is_detected_from_container_service_manager_and_parent_process(string? container, bool windowsService, string? parent, string expected)
    {
        var probe = new RuntimeProbe(name => name == "DOTNET_RUNNING_IN_CONTAINER" ? container : null, windowsService, parent);
        Assert.Equal(expected, RunningAs.Detect(probe));
    }

    [Fact]
    public void The_test_process_runs_as_a_console_program()
    {
        Assert.Equal(RunningAs.Console, RunningAs.Mode);
        Assert.False(RunningAs.IsWindowsService);
        Assert.Null(ServiceSession.Connect());
    }

    [Theory]
    [InlineData("4242 (StreamZH) S 1 4242 4242 0 -1 4194560", 1)]
    [InlineData("77 (my (odd) name) R 1234 77 77", 1234)]
    [InlineData("77 (x)", null)]
    [InlineData("garbage", null)]
    public void Parent_pid_is_read_after_the_last_parenthesis(string stat, int? expected)
    {
        Assert.Equal(expected, RunningAs.ParentPid(stat));
    }

    [Fact]
    public void The_parent_process_name_is_read_from_proc_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(RunningAs.LinuxParentProcessName()));
    }

    [Fact]
    public void Without_a_service_the_console_lifetime_stays()
    {
        var builder = WebApplication.CreateBuilder();
        builder.UseStreamZhHost(_config, null);
        builder.UseStreamZhService(null);
        using var app = builder.Build();

        Assert.IsNotType<ServiceRunLifetime>(app.Services.GetRequiredService<IHostLifetime>());
        Assert.Null(app.Services.GetService<ServiceSession>());
    }

    [Fact]
    public async Task A_restart_keeps_the_service_session_and_a_service_stop_ends_the_last_run()
    {
        var session = new ServiceSession();

        var first = Build(session, null, out var running);
        Assert.IsType<ServiceRunLifetime>(first.Services.GetRequiredService<IHostLifetime>());
        Assert.Same(session, first.Services.GetRequiredService<ServiceSession>());
        var run = first.RunUntilStoppedAsync(running, new MemoryStream());
        await Started(first);
        Assert.True(first.Services.GetRequiredService<AppLifecycle>().Restart("test"));
        var next = await run.WaitAsync(Wait);

        // Program goes on with the next run: the service was not asked to stop.
        Assert.NotNull(next);
        Assert.False(session.StopRequested);

        var second = Build(session, next, out running);
        run = second.RunUntilStoppedAsync(running, new MemoryStream());
        await Started(second);
        var stop = Task.Run(() => session.RequestStop(Wait));
        Assert.Null(await run.WaitAsync(Wait));
        Assert.True(session.StopRequested);

        // Program finishes; only then the stop reports back to the service manager.
        Assert.False(stop.IsCompleted);
        session.Finish();
        Assert.True(await stop.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_service_stop_during_a_restart_stops_the_next_run_as_soon_as_it_has_started()
    {
        var session = new ServiceSession();
        var stop = Task.Run(() => session.RequestStop(Wait));
        await WaitUntil(() => session.StopRequested);

        var app = Build(session, null, out var running);
        var started = false;
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started = true);
        Assert.Null(await app.RunUntilStoppedAsync(running, new MemoryStream()).WaitAsync(Wait));
        Assert.True(started);

        session.Finish();
        Assert.True(await stop.WaitAsync(Wait));
    }

    [Fact]
    public void A_service_stop_gives_up_waiting_after_the_timeout()
    {
        var session = new ServiceSession();
        Assert.False(session.RequestStop(TimeSpan.FromMilliseconds(50)));
        Assert.True(session.StopRequested);
        Assert.False(session.Finished);

        session.Finish();
        Assert.True(session.Finished);
        Assert.True(session.RequestStop(TimeSpan.Zero));
    }

    [Fact]
    public void The_sample_systemd_unit_matches_the_one_install_linux_writes()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "install-linux.sh")).ReplaceLineEndings("\n");
        var start = script.IndexOf("cat > \"$unit_file\" <<EOF\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "install-linux.sh writes the unit with a here-document");
        start = script.IndexOf('\n', start) + 1;
        var written = script[start..script.IndexOf("\nEOF\n", start, StringComparison.Ordinal)]
            .Replace("$user", "streamzh", StringComparison.Ordinal)
            .Replace("$group", "streamzh", StringComparison.Ordinal)
            .Replace("$data", "/var/lib/streamzh", StringComparison.Ordinal)
            .Replace("$prefix", "/opt/streamzh", StringComparison.Ordinal);

        var sample = File.ReadAllText(Path.Combine(root, "docs", "streamzh.service")).ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.Equal(Unit(sample), Unit(written));

        // The header comments differ (how to install by hand / written by the script); the unit itself must not.
        static string Unit(string text) => text[text.IndexOf("[Unit]", StringComparison.Ordinal)..];
    }

    [Fact]
    public void Install_linux_is_published_only_with_the_linux_packages()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "StreamZH.Host", "StreamZH.Host.csproj"));
        Assert.Contains("Condition=\"$(RuntimeIdentifier.StartsWith('linux'))\"", project, StringComparison.Ordinal);
        Assert.Contains("../../tools/install-linux.sh", project, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private WebApplication Build(ServiceSession session, RestartState? previous, out RunningHost running)
    {
        var builder = WebApplication.CreateBuilder();
        running = builder.UseStreamZhHost(_config, previous);
        builder.UseStreamZhService(session);
        builder.Services.AddSingleton<AppLifecycle>();
        return builder.Build();
    }

    private static async Task Started(WebApplication app)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(Wait);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "StreamZH.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Repository root (StreamZH.slnx) not found.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
