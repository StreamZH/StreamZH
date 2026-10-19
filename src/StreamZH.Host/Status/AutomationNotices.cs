// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Search;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notice of the side-by-side mode (0.9). Info level on purpose: it is a state the user chose, not a
/// problem, so it shows under System → Status and in the status window without counting as a warning in the top bar.
/// </summary>
public static class AutomationNotices
{
    public const string Key = "automation-hold";

    private const string Link = "/settings/migration";

    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<AutomationHold>() is not { IsOn: true })
        {
            return;
        }

        notices.Add(new OngoingNotice(
            Key,
            NoticeLevels.Info,
            "Side-by-side mode: StreamZH grabs nothing automatically",
            "Sonarr and Radarr keep grabbing. Search and grab by hand still work. Switch it off in Settings → Migration when you move over.",
            Link,
            false));
    }

    /// <summary>For the top bar: true while the side-by-side mode is on.</summary>
    public static bool IsOn(IServiceProvider services) => services.GetService<AutomationHold>()?.IsOn == true;
}
