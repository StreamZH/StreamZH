// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using StreamZH.Tray.Core;

namespace StreamZH.Tray;

/// <summary>
/// The icon in the notification area and its menu. Polls /api/v1/tray/status every 5 s (every second for half a
/// minute after a command), shows the state as a coloured dot and in the tooltip, and a balloon when a download
/// finishes or fails.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FastFor = TimeSpan.FromSeconds(30);

    private readonly string _dataDirectory;
    private readonly TrayIcons _icons = new();
    private readonly TrayClient _client = TrayClient.Create();
    private readonly StreamZhProcess _process;
    private readonly TraySettings _settings;
    private readonly TrayStateResolver _resolver = new(TimeProvider.System);
    private readonly NotificationTracker _tracker = new();
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _open = new("Open dashboard");
    private readonly ToolStripMenuItem _status = new("Not running") { Enabled = false };
    private readonly ToolStripMenuItem _setupCode = new("Show setup code") { Visible = false };
    private readonly ToolStripMenuItem _pause = new("Pause downloads");
    private readonly ToolStripMenuItem _restart = new("Restart StreamZH");
    private readonly ToolStripMenuItem _stop = new("Stop StreamZH");
    private readonly ToolStripMenuItem _start = new("Start StreamZH");
    private readonly ToolStripMenuItem _logs = new("Open logs");
    private readonly ToolStripMenuItem _data = new("Open data folder");
    private readonly ToolStripMenuItem _autostart = new("Start with Windows") { CheckOnClick = false };
    private readonly ToolStripMenuItem _notifications = new("Notifications") { CheckOnClick = false };
    private readonly ToolStripMenuItem _close = new("Close tray icon");
    private readonly System.Windows.Forms.Timer _timer = new();

    private TrayConnection _connection;
    private TrayState _state = new(TrayStateKind.NotRunning);
    private DateTimeOffset _shownStartupError;
    private DateTimeOffset _fastUntil = DateTimeOffset.MinValue;
    private bool _polling;

    public TrayContext(string[] args)
    {
        _dataDirectory = TrayFiles.DataDirectory(args);
        _connection = TrayFiles.Load(_dataDirectory);
        _process = new StreamZhProcess(_dataDirectory);
        var startHost = TrayCommandLine.WantsStart(args);
        _settings = new TraySettings(_dataDirectory, startHost);

        _open.Font = new Font(_open.Font, FontStyle.Bold);
        _open.Click += (_, _) => OpenDashboard();
        _setupCode.Click += (_, _) => ShowSetupCode();
        _pause.Click += (_, _) => Run(PauseOrResumeAsync);
        _restart.Click += (_, _) => Run(RestartAsync);
        _stop.Click += (_, _) => Run(StopAsync);
        _start.Click += (_, _) => Run(StartAsync);
        _logs.Click += (_, _) => OpenFolder(_connection.LogDirectory);
        _data.Click += (_, _) => OpenFolder(_dataDirectory);
        _autostart.Click += (_, _) => ToggleAutostart();
        _notifications.Click += (_, _) => _settings.Notifications = !_settings.Notifications;
        _close.Click += (_, _) => ExitThread();

        _menu.Items.AddRange(
        [
            _open,
            _status,
            _setupCode,
            new ToolStripSeparator(),
            _pause,
            _restart,
            _stop,
            _start,
            new ToolStripSeparator(),
            _logs,
            _data,
            new ToolStripSeparator(),
            _autostart,
            _notifications,
            new ToolStripSeparator(),
            _close,
        ]);
        _menu.Opening += (_, _) =>
        {
            _autostart.Checked = _settings.StartWithWindows;
            _notifications.Checked = _settings.Notifications;
            Run(PollAsync);
        };

        _notify = new NotifyIcon
        {
            Icon = _icons.For(StatusDot.Red),
            Text = "StreamZH",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenDashboard();
            }
        };

        Apply(_state);

        // "Tray only" installs (no service): StreamZH starts with the tray at sign-in.
        if (startHost && !_process.IsService && _process.State() == ProcessState.Stopped && _process.Start() is { } problem)
        {
            Error(problem);
        }

        _timer.Interval = (int)SlowPoll.TotalMilliseconds;
        _timer.Tick += (_, _) => Run(PollAsync);
        _timer.Start();
        Run(PollAsync);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _notify.Visible = false;
            _notify.Dispose();
            _menu.Dispose();
            _icons.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notify.Visible = false;
        base.ExitThreadCore();
    }

    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            var (run, after) = _tracker.Query;
            var (connection, process, probe, startupError) = await Task.Run(async () =>
            {
                var connection = TrayFiles.Load(_dataDirectory);
                var process = _process.State();
                var probe = process == ProcessState.Running ? await _client.StatusAsync(connection, run, after).ConfigureAwait(false) : null;
                var startupError = process == ProcessState.Stopped ? TrayFiles.ReadStartupError(_dataDirectory) : null;
                return (connection, process, probe, startupError);
            }).ConfigureAwait(true);

            _connection = connection;
            var state = _resolver.Resolve(process, probe, connection.TokenProblem);
            if (state.Kind == TrayStateKind.NotRunning && startupError is not null)
            {
                // 1.0 RC: StreamZH could not start (e.g. the port is in use): say why, once per failed start.
                state = state with { StartupError = startupError };
                if (startupError.At != _shownStartupError)
                {
                    _shownStartupError = startupError.At;
                    _notify.ShowBalloonTip(10000, "StreamZH could not start", StatusText.Truncate(startupError.Message, 255), ToolTipIcon.Error);
                }
            }
            if (state.Status is { } status)
            {
                var finished = _tracker.Take(status);
                if (finished.Count > 0 && _settings.Notifications)
                {
                    foreach (var (title, text, error) in NotificationTracker.Messages(finished))
                    {
                        _notify.ShowBalloonTip(5000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
                    }
                }
            }

            Apply(state);
        }
        finally
        {
            _polling = false;
            var interval = DateTimeOffset.UtcNow < _fastUntil ? FastPoll : SlowPoll;
            _timer.Interval = (int)interval.TotalMilliseconds;
        }
    }

    private void Apply(TrayState state)
    {
        _state = state;
        _notify.Icon = _icons.For(state.Dot);
        _notify.Text = StatusText.Tooltip(state);
        _status.Text = StatusText.Line(state);

        var answering = state.Kind is TrayStateKind.Running or TrayStateKind.Paused;
        _open.Enabled = state.IsUp || state.Kind == TrayStateKind.Starting;
        _pause.Visible = answering;
        _pause.Text = state.Kind == TrayStateKind.Paused ? "Resume downloads" : "Pause downloads";
        _restart.Visible = state.Kind != TrayStateKind.NotRunning;
        _restart.Enabled = state.IsUp;
        _stop.Visible = state.Kind != TrayStateKind.NotRunning;
        _stop.Enabled = state.Kind != TrayStateKind.Stopping;
        _start.Visible = state.Kind == TrayStateKind.NotRunning;
        _start.Enabled = _process.CanStart;

        // 0.12: until the owner account exists, setup from another device needs the one-time setup code.
        _setupCode.Visible = state.IsUp && state.Status?.SetupCode is not null;
    }

    private void ShowSetupCode()
    {
        if (_state.Status?.SetupCode is not { } code)
        {
            return;
        }

        try
        {
            Clipboard.SetText(code);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is busy; the code is still shown.
        }

        MessageBox.Show(
            $"Setup code: {code}\n\nOn this computer the setup page does not need it. When you create the owner account from another device (phone, other PC), the setup page asks for this code. It is copied to the clipboard.",
            "StreamZH setup code",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task PauseOrResumeAsync()
    {
        var ok = _state.Kind == TrayStateKind.Paused ? await _client.ResumeAsync(_connection) : await _client.PauseAsync(_connection);
        if (!ok)
        {
            Error("StreamZH did not accept the command. Try again in a moment, or open the dashboard.");
        }

        await AfterCommandAsync();
    }

    private async Task RestartAsync()
    {
        if (!await _client.RestartAsync(_connection))
        {
            if (!_process.IsService)
            {
                Error("StreamZH did not answer. Stop it and start it again.");
                return;
            }

            if (await _process.RestartServiceAsync() is { } problem)
            {
                Error($"The service could not be restarted: {problem}");
            }
        }

        _resolver.Restarting();
        await AfterCommandAsync();
    }

    private async Task StopAsync()
    {
        var answer = MessageBox.Show(
            "Stop StreamZH? Downloads, searches and imports pause until you start it again.",
            "StreamZH",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK)
        {
            return;
        }

        // The service through Windows; without the right to (or in a console window) StreamZH shuts itself down.
        if (!(_process.IsService && _process.TryStopService()) && !await _client.ShutdownAsync(_connection))
        {
            Error("StreamZH did not answer. If it hangs, end StreamZH.exe in the Task Manager.");
        }

        await AfterCommandAsync();
    }

    private async Task StartAsync()
    {
        if (_process.Start() is { } problem)
        {
            Error(problem);
            return;
        }

        _resolver.Restarting();
        await AfterCommandAsync();
    }

    private Task AfterCommandAsync()
    {
        _fastUntil = DateTimeOffset.UtcNow + FastFor;
        _timer.Interval = (int)FastPoll.TotalMilliseconds;
        return PollAsync();
    }

    private void OpenDashboard()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_connection.Endpoint.DashboardUrl) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Error($"The browser could not be opened: {ex.Message}");
        }
    }

    private void OpenFolder(string folder)
    {
        var target = Directory.Exists(folder) ? folder : _dataDirectory;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", TrayCommandLine.Quote(target)) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Error($"{target} could not be opened: {ex.Message}");
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Error($"Start with Windows could not be changed: {ex.Message}");
        }
    }

    private void Error(string text) => _notify.ShowBalloonTip(8000, "StreamZH", text, ToolTipIcon.Warning);

    // Menu and timer handlers are fire-and-forget; an unexpected error shows as a balloon instead of ending the tray.
    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Error(ex.Message);
        }
    }
}
