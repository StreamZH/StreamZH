// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Host.General;
using StreamZH.Host.Security;

namespace StreamZH.Host.Status;

/// <summary>Status-window notices of the 0.12 security settings (security review S1-06).</summary>
public static class SecurityNotices
{
    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);

        // 0.12 (SV2 N-02): the program folder is open to every user of this computer.
        if (services.GetService<AppFolderCheck>() is { Problem: { } appFolder })
        {
            notices.Add(new OngoingNotice("app-folder-open", NoticeLevels.Warn, "Program folder not protected", appFolder, "/system/status", false));
        }

        if (services.GetService<SecuritySettings>() is not { } security || services.GetService<AccessSettings>() is not { } access)
        {
            return;
        }

        if (security.Method != AuthenticationMethod.External
            && security.Required == AuthenticationRequired.DisabledForLocalAddresses
            && ContainerDetection.IsContainer
            && access.TrustedLocalNetworks.IsEmpty)
        {
            notices.Add(new OngoingNotice(
                "auth-local-container",
                NoticeLevels.Warn,
                "No sign-in for local addresses in a container",
                "With published ports every visitor can come from the container network and count as local. Require sign-in, or set Trusted local networks.",
                "/settings/general#security",
                true));
        }
    }
}
