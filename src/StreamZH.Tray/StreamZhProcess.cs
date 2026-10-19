// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using StreamZH.Core;
using StreamZH.Tray.Core;

namespace StreamZH.Tray;

/// <summary>
/// Starts and stops StreamZH: the Windows service "StreamZH" when it is installed (tools\Install-Service.ps1 lets the
/// installing user start and stop it), else StreamZH.exe next to this exe in a console window.
/// </summary>
internal sealed class StreamZhProcess(string dataDirectory)
{
    public const string ServiceName = "StreamZH";
    private const int AccessDenied = 5;
    private const int Cancelled = 1223;

    public string HostExe { get; } = Path.Combine(AppContext.BaseDirectory, "StreamZH.exe");

    /// <summary>True when the service exists (asked again each time: it may be installed or removed while the tray runs).</summary>
    public bool IsService
    {
        get
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                _ = service.Status;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public bool CanStart => IsService || File.Exists(HostExe);

    public ProcessState State()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.Paused => ProcessState.Running,
                ServiceControllerStatus.StartPending => ProcessState.StartPending,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ProcessState.StopPending,
                _ => ProcessState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            // No service: a StreamZH.exe in any session counts.
            var running = Process.GetProcessesByName("StreamZH");
            try
            {
                return running.Length > 0 ? ProcessState.Running : ProcessState.Stopped;
            }
            finally
            {
                foreach (var process in running)
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Starts the service (asks for administrator rights when this user may not), or StreamZH.exe. Null or an error text.</summary>
    public string? Start()
    {
        if (IsService)
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                service.Start();
                return null;
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: AccessDenied })
            {
                return StartElevated();
            }
            catch (InvalidOperationException ex)
            {
                return ex.InnerException?.Message ?? ex.Message;
            }
        }

        if (!File.Exists(HostExe))
        {
            return $"{HostExe} is missing.";
        }

        // Never as SYSTEM (SV4 X-05): StreamZH.exe would inherit this account.
        using (var me = WindowsIdentity.GetCurrent())
        {
            if (TrayCommandLine.HostStartRefusal(me.User?.Value) is { } refused)
            {
                return refused;
            }
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(HostExe)
            {
                Arguments = TrayCommandLine.HostArguments(dataDirectory, AppPaths.Resolve().DataDirectory),
                WorkingDirectory = AppContext.BaseDirectory,
                // No console window at all (CREATE_NO_WINDOW): StreamZH logs to its log file, the tray shows its state.
                // ShellExecute with a hidden or minimized window still shows one when Windows Terminal is the default
                // console host.
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return null;
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Stops the service through Windows. False when this user may not (the caller then asks StreamZH to shut down,
    /// which also ends the service cleanly) or when there is no service.
    /// </summary>
    public bool TryStopService()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                return true;
            }

            service.Stop();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Restart through Windows (when StreamZH does not answer): stop, wait, start.</summary>
    public async Task<string?> RestartServiceAsync()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60))).ConfigureAwait(true);
            }

            service.Start();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }

    // sc.exe start in an elevated process: Windows shows the UAC prompt.
    private static string? StartElevated()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("sc.exe", $"start {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return "Not started: administrator rights were not granted.";
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }
}
