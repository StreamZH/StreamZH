// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Sockets;
using StreamZH.Core;
using StreamZH.Core.Tray;
using StreamZH.Host.General;
using StreamZH.Tray.Core;

namespace StreamZH.Tests.Host;

/// <summary>1.0 RC: a port in use at start is one clear line (console, log, event log, tray) and an exit code, no stack trace.</summary>
[Collection(nameof(ExitCodeCollection))]
public sealed class StartupCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"streamzh-startup-{Guid.NewGuid():N}");

    public StartupCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.ExitCode = 0;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Addresses_become_endpoints_like_kestrel_binds_them()
    {
        var endpoints = StartupCheck.Endpoints(["http://*:9797", "https://192.168.1.10:9798", "http://localhost:5000", "http://[::1]:7000", "http://+", "Kestrel endpoints in appsettings.json"]);

        Assert.Equal(
            [(IPAddress.IPv6Any, 9797), (IPAddress.Parse("192.168.1.10"), 9798), (IPAddress.Loopback, 5000), (IPAddress.IPv6Loopback, 7000), (IPAddress.IPv6Any, 80)],
            endpoints.Select(e => (e.Address, e.Port)));
    }

    [Fact]
    public void A_port_in_use_says_which_port_and_how_to_choose_another()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var port = ((IPEndPoint)other.LocalEndpoint).Port;

        var fromSettings = StartupCheck.Problem([new StartupCheck.Endpoint(IPAddress.Loopback, port, $"http://127.0.0.1:{port}")], explicitUrls: null);
        Assert.NotNull(fromSettings);
        Assert.Equal($"Port {port} is already in use", fromSettings.Value.Summary);
        Assert.StartsWith($"StreamZH could not start: port {port} is already in use by another program", fromSettings.Value.Message, StringComparison.Ordinal);
        Assert.Contains($"--urls http://127.0.0.1:{port + 10} ", fromSettings.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("*", fromSettings.Value.Message, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS", fromSettings.Value.Message, StringComparison.Ordinal);
        Assert.Contains("Settings → General → Host", fromSettings.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.NewLine, fromSettings.Value.Message, StringComparison.Ordinal);

        var fromUrls = StartupCheck.Problem(StartupCheck.Endpoints([$"http://127.0.0.1:{port}"]), $"http://127.0.0.1:{port}");
        Assert.Contains($"started with the addresses http://127.0.0.1:{port}", fromUrls!.Value.Message, StringComparison.Ordinal);
        Assert.EndsWith($"for example http://127.0.0.1:{port + 10}.", fromUrls.Value.Message, StringComparison.Ordinal);

        // SV4 X-09: a localhost-only start (behind a reverse proxy) is never told to listen on every address.
        var localhost = StartupCheck.Problem(StartupCheck.Endpoints([$"http://localhost:{port}"]), $"http://localhost:{port}");
        Assert.EndsWith($"for example http://localhost:{port + 10}.", localhost!.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("*", localhost.Value.Message, StringComparison.Ordinal);

        other.Stop();
        Assert.Null(StartupCheck.Problem([new StartupCheck.Endpoint(IPAddress.Loopback, port, $"http://127.0.0.1:{port}")], null));
    }

    [Theory]
    [InlineData("http://localhost:7301", 7311, "http://localhost:7311")]
    [InlineData("http://127.0.0.1:7301", 7311, "http://127.0.0.1:7311")]
    [InlineData("https://[::1]:9798/streamzh", 9808, "https://[::1]:9808/streamzh")]
    [InlineData("http://*:9797", 9807, "http://*:9807")]
    [InlineData("http://media.example.lan/base", 90, "http://media.example.lan:90/base")]
    [InlineData("HTTPS://192.168.1.10:9798", 9808, "HTTPS://192.168.1.10:9808")]
    [InlineData(" http://localhost:5000 ", 5010, "http://localhost:5010")]
    [InlineData("Kestrel endpoints in appsettings.json", 9807, "http://localhost:9807")]
    public void The_suggestion_changes_only_the_port(string url, int port, string expected) =>
        Assert.Equal(expected, StartupCheck.WithPort(url, port));

    [Fact]
    public void Other_bind_errors_have_their_own_text()
    {
        var endpoint = new StartupCheck.Endpoint(IPAddress.Parse("192.168.1.10"), 9797, "http://192.168.1.10:9797");

        Assert.Equal("The address 192.168.1.10 is not on this computer", StartupCheck.Describe(endpoint, SocketError.AddressNotAvailable, null).Summary);
        Assert.Contains("ports below 1024", StartupCheck.Describe(endpoint with { Port = 80 }, SocketError.AccessDenied, null).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_sets_the_exit_code_and_leaves_the_reason_for_the_tray_until_the_next_start()
    {
        var paths = new AppPaths(_dir);
        var console = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            StartupCheck.Report("Port 9797 is already in use", "StreamZH could not start: port 9797 is already in use.", paths, log: null, service: null);
        }
        finally
        {
            Console.SetError(console);
        }

        Assert.Equal(StartupCheck.ExitCode, Environment.ExitCode);
        Assert.Equal("StreamZH could not start: port 9797 is already in use." + Environment.NewLine, captured.ToString());
        var error = TrayFiles.ReadStartupError(_dir);
        Assert.Equal("Port 9797 is already in use", error!.Summary);
        Assert.Equal("Not running · Port 9797 is already in use", StatusText.Line(new TrayState(TrayStateKind.NotRunning, StartupError: error)));
        Assert.True(File.Exists(Path.Combine(_dir, TrayContract.StartupErrorFile)));

        StartupCheck.Clear(paths);
        Assert.Null(TrayFiles.ReadStartupError(_dir));
        File.WriteAllText(Path.Combine(_dir, TrayContract.StartupErrorFile), "{ not json");
        Assert.Null(TrayFiles.ReadStartupError(_dir));
    }
}

/// <summary>Tests that set <see cref="Environment.ExitCode"/> do not run in parallel with each other.</summary>
[CollectionDefinition(nameof(ExitCodeCollection), DisableParallelization = true)]
public sealed class ExitCodeCollection;
