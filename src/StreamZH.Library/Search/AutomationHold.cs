// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Core.Indexers;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>The side-by-side mode as the API shows it: on/off and since when (UTC).</summary>
public sealed record AutomationHoldState(bool Hold, DateTime? Since);

/// <summary>
/// Side-by-side mode (0.9): while it is on, StreamZH grabs nothing by itself — no RSS sync, no automatic search (on add,
/// missing, cutoff unmet, scheduled, after a failed download, from import lists). Searching by hand and grabbing a
/// release by hand still work. Meant for the time StreamZH runs next to Sonarr/Radarr, which still do the grabbing.
/// <para>
/// The one hard gate is <see cref="GrabService"/>: a grab made in an automatic flow (<see cref="IndexerCallContext"/>
/// is <see cref="IndexerCallSource.Automatic"/> or <see cref="IndexerCallSource.Rss"/>) is refused. RSS sync and the
/// automatic search also stop before they ask the indexers, so the API limits shared with the old apps are not used up.
/// </para>
/// </summary>
public sealed class AutomationHold(ConfigRepository config, TimeProvider? time = null, IEventBus? events = null, ILogger<AutomationHold>? logger = null)
{
    /// <summary>The skip reason of an automatic search that did not run (see <see cref="AutomaticSearchFinished"/>).</summary>
    public const string SkipReason = "automationHold";

    public const string Message = "Side-by-side mode is on: StreamZH grabs nothing automatically. Search and grab by hand, or switch the mode off in Settings → Migration.";

    public bool IsOn => string.Equals(config.Get(ConfigKeys.AutomationHold), "true", StringComparison.OrdinalIgnoreCase);

    public AutomationHoldState State()
    {
        var on = IsOn;
        DateTime? since = on && DateTime.TryParse(config.Get(ConfigKeys.AutomationHoldSince), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;
        return new AutomationHoldState(on, since);
    }

    /// <summary>True for the flows that grab without the user picking a release.</summary>
    public static bool IsAutomaticFlow => IndexerCallContext.Current is IndexerCallSource.Automatic or IndexerCallSource.Rss;

    /// <summary>True when an automatic grab in the current flow must not happen.</summary>
    public bool Blocks => IsAutomaticFlow && IsOn;

    /// <summary>Switches the mode; switching it on again keeps the first "since".</summary>
    public AutomationHoldState Set(bool hold)
    {
        var before = State();
        if (before.Hold == hold)
        {
            return before;
        }

        if (hold)
        {
            var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            config.Set(ConfigKeys.AutomationHoldSince, now.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            config.Set(ConfigKeys.AutomationHoldSince, string.Empty);
        }

        config.Set(ConfigKeys.AutomationHold, hold ? "true" : "false");
        logger?.LogInformation(hold
            ? "Side-by-side mode on: StreamZH grabs nothing automatically"
            : "Side-by-side mode off: RSS sync and automatic searches grab again");
        if (events is not null)
        {
            _ = events.PublishAsync(new OngoingChanged()).AsTask();
        }

        return State();
    }
}
