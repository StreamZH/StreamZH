// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Tray;

/// <summary>StreamZH.Tray.exe [--data &lt;dir&gt;]: the StreamZH icon in the notification area, one per Windows session.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // SV4 X-05: never as SYSTEM (a silent install by a deployment tool runs as SYSTEM, in a session nobody sees):
        // the tray would start StreamZH as LocalSystem. A signed-in user's Run entry starts it as that user.
        using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (global::StreamZH.Tray.Core.TrayCommandLine.HostStartRefusal(me.User?.Value) is not null)
            {
                return 1;
            }
        }

        // One icon per signed-in user: a second start (Run entry plus installer) ends at once.
        using var single = new Mutex(initiallyOwned: true, @"Local\StreamZH.Tray", out var first);
        if (!first)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(args);
        Application.Run(context);
        return 0;
    }
}
