// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Win32;
using StreamZH.Core;
using StreamZH.Tray.Core;

namespace StreamZH.Tray;

/// <summary>Per-user choices of the tray, in HKCU: "Start with Windows" (the Run entry) and "Notifications".</summary>
internal sealed class TraySettings(string dataDirectory)
{
    private const string Key = @"Software\StreamZH\Tray";
    private const string NotificationsValue = "Notifications";

    public bool StartWithWindows
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(TrayCommandLine.RunKey);
            return run?.GetValue(TrayCommandLine.RunValueName) is string { Length: > 0 };
        }
        set
        {
            using var run = Registry.CurrentUser.CreateSubKey(TrayCommandLine.RunKey);
            if (value)
            {
                run.SetValue(TrayCommandLine.RunValueName, TrayCommandLine.RunEntry(Environment.ProcessPath ?? Application.ExecutablePath, dataDirectory, AppPaths.Resolve().DataDirectory));
            }
            else
            {
                run.DeleteValue(TrayCommandLine.RunValueName, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>Balloon when a download finishes or fails; on unless switched off in the menu.</summary>
    public bool Notifications
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(NotificationsValue) is not int value || value != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(NotificationsValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
