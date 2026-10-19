// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using StreamZH.Core.Downloads;

namespace StreamZH.Core.Decisions;

/// <summary>0.11: which protocol ranks first when quality and scores are equal (Radarr's delay profile "Preferred protocol").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProtocolPreference>))]
public enum ProtocolPreference
{
    [JsonStringEnumMemberName("usenet")]
    Usenet,

    [JsonStringEnumMemberName("torrent")]
    Torrent,

    [JsonStringEnumMemberName("noPreference")]
    NoPreference,
}

/// <summary>
/// Release rules for all indexers (Settings → Indexers, like Radarr's "Indexers → Options" plus Sonarr's release
/// profiles). <see cref="None"/> switches every rule off; the app reads the real values from the settings.
/// </summary>
public sealed record ReleaseRules
{
    /// <summary>No rules: every option off or unlimited.</summary>
    public static ReleaseRules None { get; } = new();

    /// <summary>Usenet propagation: releases younger than this many minutes are rejected for now (0 = off).</summary>
    public int MinimumAgeMinutes { get; init; }

    /// <summary>Releases older than this many days are rejected (0 = unlimited), like the provider's retention.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Releases larger than this many MB are rejected (0 = unlimited), on top of the quality size limits.</summary>
    public int MaximumSizeMb { get; init; }

    /// <summary>False: releases with hardcoded subtitles (HC, KORSUB …) are rejected unless the tag is whitelisted.</summary>
    public bool AllowHardcodedSubs { get; init; } = true;

    /// <summary>Subtitle tags that are not counted as hardcoded (e.g. "DKSUBS"); case-insensitive.</summary>
    public IReadOnlyList<string> WhitelistedHardcodedSubs { get; init; } = [];

    /// <summary>Must contain, must not contain and preferred terms (only enabled profiles are used).</summary>
    public IReadOnlyList<ReleaseProfile> ReleaseProfiles { get; init; } = [];

    /// <summary>0.11: ranks releases of this protocol first when quality and scores are equal (default Usenet, like Radarr).</summary>
    public ProtocolPreference PreferredProtocol { get; init; } = ProtocolPreference.Usenet;

    /// <summary>0.11: automatic grabs of Usenet releases wait until they are this many minutes old (0 = at once).</summary>
    public int UsenetDelayMinutes { get; init; }

    /// <summary>0.11: automatic grabs of torrents wait until they are this many minutes old (0 = at once).</summary>
    public int TorrentDelayMinutes { get; init; }

    /// <summary>A release of the preferred protocol in the profile's best allowed quality is not delayed (Radarr's default).</summary>
    public bool BypassDelayIfHighestQuality { get; init; } = true;

    /// <summary>The delay of a protocol in minutes.</summary>
    public int DelayFor(DownloadProtocol protocol) => protocol == DownloadProtocol.Torrent ? TorrentDelayMinutes : UsenetDelayMinutes;

    public bool IsPreferred(DownloadProtocol protocol) => PreferredProtocol switch
    {
        ProtocolPreference.Usenet => protocol == DownloadProtocol.Usenet,
        ProtocolPreference.Torrent => protocol == DownloadProtocol.Torrent,
        _ => false,
    };

    /// <summary>The same rules without delays: searches started by the user are never held back (Radarr's UserInvokedSearch).</summary>
    public ReleaseRules WithoutDelays() => UsenetDelayMinutes == 0 && TorrentDelayMinutes == 0 ? this : this with { UsenetDelayMinutes = 0, TorrentDelayMinutes = 0 };
}
