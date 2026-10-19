// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Serialization;
using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Data;

namespace StreamZH.Library.Search;

/// <summary>Radarr's minimum availability: from when a movie may be grabbed automatically.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MinimumAvailability>))]
public enum MinimumAvailability
{
    /// <summary>As soon as it is in the library.</summary>
    [JsonStringEnumMemberName("announced")]
    Announced,

    /// <summary>From the cinema release (TMDB release date); the old StreamZH rule.</summary>
    [JsonStringEnumMemberName("inCinemas")]
    InCinemas,

    /// <summary>From the home release: the digital or physical release, else cinema release + 90 days (Radarr's estimate).</summary>
    [JsonStringEnumMemberName("released")]
    Released,
}

/// <summary>
/// All values of Settings → Indexers (options and automation). Sizes in MB, ages in minutes or days. 0.11: preferred
/// protocol and the delays (minutes) for automatic grabs of Usenet releases and torrents (Radarr's delay profile).
/// </summary>
public sealed record IndexerOptionValues(
    int MinimumAge,
    int Retention,
    int MaximumSize,
    bool AllowHardcodedSubs,
    IReadOnlyList<string> WhitelistedHardcodedSubs,
    MinimumAvailability MinimumAvailability,
    int AvailabilityDelay,
    int RssSyncInterval,
    int MissingSearchInterval,
    bool SearchOnAdd,
    bool SearchAlternativeTitles = true,
    ProtocolPreference PreferredProtocol = ProtocolPreference.Usenet,
    int UsenetDelay = 0,
    int TorrentDelay = 0,
    bool BypassDelayIfHighestQuality = true);

/// <summary>
/// Settings → Indexers: release rules for all indexers (Radarr's "Indexers → Options"), release profiles, RSS sync and
/// the automatic searches. Read from the config table on every use, so changes apply at once.
/// </summary>
public sealed class IndexerOptions(ConfigRepository config, ReleaseProfileRepository profiles)
{
    public const int MaxMinimumAge = 100_000;
    public const int MaxRetention = 100_000;
    public const int MaxMaximumSize = 10_000_000;
    public const int MaxAvailabilityDelay = 365;

    /// <summary>Protocol delays in minutes: 0 to one week (pending releases are kept for two weeks).</summary>
    public const int MaxProtocolDelay = 10_080;

    /// <summary>RSS sync interval in minutes: 0 (off) or 10 to 120 like Radarr/Sonarr. Default 60 (Radarr's default).</summary>
    public const int DefaultRssSyncInterval = 60;
    public const int MinRssSyncInterval = 10;
    public const int MaxRssSyncInterval = 120;

    /// <summary>Search for missing items every N hours: 0 (off, default) or 6 to 720, so indexer API limits are not burnt.</summary>
    public const int MinMissingSearchInterval = 6;
    public const int MaxMissingSearchInterval = 720;

    /// <summary>Radarr: without a digital or physical release date a movie counts as released 90 days after the cinema release.</summary>
    public const int ReleasedAfterCinemaDays = 90;

    public static readonly IndexerOptionValues Defaults = new(
        MinimumAge: 0,
        Retention: 0,
        MaximumSize: 0,
        AllowHardcodedSubs: false,
        WhitelistedHardcodedSubs: [],
        MinimumAvailability: MinimumAvailability.InCinemas,
        AvailabilityDelay: 0,
        RssSyncInterval: DefaultRssSyncInterval,
        MissingSearchInterval: 0,
        SearchOnAdd: false,
        SearchAlternativeTitles: true,
        PreferredProtocol: ProtocolPreference.Usenet,
        UsenetDelay: 0,
        TorrentDelay: 0,
        BypassDelayIfHighestQuality: true);

    public IndexerOptionValues Values => new(
        Int(ConfigKeys.IndexerMinimumAge, Defaults.MinimumAge, 0, MaxMinimumAge),
        Int(ConfigKeys.IndexerRetention, Defaults.Retention, 0, MaxRetention),
        Int(ConfigKeys.IndexerMaximumSize, Defaults.MaximumSize, 0, MaxMaximumSize),
        Bool(ConfigKeys.IndexerAllowHardcodedSubs, Defaults.AllowHardcodedSubs),
        SplitTags(config.Get(ConfigKeys.IndexerWhitelistedHardcodedSubs)),
        Enum.TryParse<MinimumAvailability>(config.Get(ConfigKeys.IndexerMinimumAvailability), ignoreCase: true, out var availability)
            && Enum.IsDefined(availability)
            ? availability
            : Defaults.MinimumAvailability,
        Int(ConfigKeys.IndexerAvailabilityDelay, Defaults.AvailabilityDelay, -MaxAvailabilityDelay, MaxAvailabilityDelay),
        RssInterval(),
        MissingInterval(),
        Bool(ConfigKeys.IndexerSearchOnAdd, Defaults.SearchOnAdd),
        Bool(ConfigKeys.IndexerSearchAlternativeTitles, Defaults.SearchAlternativeTitles),
        Enum.TryParse<ProtocolPreference>(config.Get(ConfigKeys.IndexerPreferredProtocol), ignoreCase: true, out var preferred)
            && Enum.IsDefined(preferred)
            && !int.TryParse(config.Get(ConfigKeys.IndexerPreferredProtocol), out _)
            ? preferred
            : Defaults.PreferredProtocol,
        Int(ConfigKeys.IndexerUsenetDelay, Defaults.UsenetDelay, 0, MaxProtocolDelay),
        Int(ConfigKeys.IndexerTorrentDelay, Defaults.TorrentDelay, 0, MaxProtocolDelay),
        Bool(ConfigKeys.IndexerBypassDelayIfHighestQuality, Defaults.BypassDelayIfHighestQuality));

    /// <summary>RSS sync interval; null when switched off.</summary>
    public TimeSpan? RssSyncInterval => RssInterval() is > 0 and var minutes ? TimeSpan.FromMinutes(minutes) : null;

    /// <summary>Interval of the scheduled search for missing items; null when switched off (default).</summary>
    public TimeSpan? MissingSearchInterval => MissingInterval() is > 0 and var hours ? TimeSpan.FromHours(hours) : null;

    public bool SearchOnAdd => Bool(ConfigKeys.IndexerSearchOnAdd, Defaults.SearchOnAdd);

    /// <summary>Text searches also use alternative titles (German title, scene names, yours); at most three titles per search.</summary>
    public bool SearchAlternativeTitles => Bool(ConfigKeys.IndexerSearchAlternativeTitles, Defaults.SearchAlternativeTitles);

    /// <summary>The release rules for the decision engine, with the release profiles.</summary>
    public ReleaseRules Rules()
    {
        var values = Values;
        return new ReleaseRules
        {
            MinimumAgeMinutes = values.MinimumAge,
            RetentionDays = values.Retention,
            MaximumSizeMb = values.MaximumSize,
            AllowHardcodedSubs = values.AllowHardcodedSubs,
            WhitelistedHardcodedSubs = values.WhitelistedHardcodedSubs,
            ReleaseProfiles = [.. profiles.All().Where(p => p.Enabled)],
            PreferredProtocol = values.PreferredProtocol,
            UsenetDelayMinutes = values.UsenetDelay,
            TorrentDelayMinutes = values.TorrentDelay,
            BypassDelayIfHighestQuality = values.BypassDelayIfHighestQuality,
        };
    }

    /// <summary>Saves values that were validated by the caller.</summary>
    public void Save(IndexerOptionValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Set(ConfigKeys.IndexerMinimumAge, values.MinimumAge);
        Set(ConfigKeys.IndexerRetention, values.Retention);
        Set(ConfigKeys.IndexerMaximumSize, values.MaximumSize);
        config.Set(ConfigKeys.IndexerAllowHardcodedSubs, values.AllowHardcodedSubs ? "true" : "false");
        config.Set(ConfigKeys.IndexerWhitelistedHardcodedSubs, string.Join(',', values.WhitelistedHardcodedSubs.Select(t => t.Trim()).Where(t => t.Length > 0)));
        config.Set(ConfigKeys.IndexerMinimumAvailability, values.MinimumAvailability.ToString());
        Set(ConfigKeys.IndexerAvailabilityDelay, values.AvailabilityDelay);
        Set(ConfigKeys.IndexerRssSyncInterval, values.RssSyncInterval);
        Set(ConfigKeys.IndexerMissingSearchInterval, values.MissingSearchInterval);
        config.Set(ConfigKeys.IndexerSearchOnAdd, values.SearchOnAdd ? "true" : "false");
        config.Set(ConfigKeys.IndexerSearchAlternativeTitles, values.SearchAlternativeTitles ? "true" : "false");
        config.Set(ConfigKeys.IndexerPreferredProtocol, values.PreferredProtocol.ToString());
        Set(ConfigKeys.IndexerUsenetDelay, values.UsenetDelay);
        Set(ConfigKeys.IndexerTorrentDelay, values.TorrentDelay);
        config.Set(ConfigKeys.IndexerBypassDelayIfHighestQuality, values.BypassDelayIfHighestQuality ? "true" : "false");
    }

    /// <summary>
    /// From when a movie counts as available, like Radarr's Movie.IsAvailable: Announced = always; In cinemas = the TMDB
    /// release date; Released = the earlier of the digital and physical release, else the cinema release + 90 days; plus
    /// the availability delay (days, may be negative). Without a release date the TMDB status decides (Released, or a
    /// year before this one), as before 0.6.
    /// Null = available now; <see cref="DateTimeOffset.MaxValue"/> = not available and no date known.
    /// </summary>
    public static DateTimeOffset? AvailableFrom(Movie movie, MinimumAvailability minimum, int delayDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(movie);
        if (minimum == MinimumAvailability.Announced)
        {
            return null;
        }

        // Released: the digital or physical release when TMDB knows one (Radarr), else the cinema release + 90 days.
        if (minimum == MinimumAvailability.Released && HomeRelease(movie) is { } home)
        {
            var homeFrom = new DateTimeOffset(home.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(delayDays);
            return now >= homeFrom ? null : homeFrom;
        }

        if (DateOnly.TryParseExact(movie.InCinemas, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cinema))
        {
            var from = new DateTimeOffset(cinema.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                .AddDays((minimum == MinimumAvailability.Released ? ReleasedAfterCinemaDays : 0) + delayDays);
            return now >= from ? null : from;
        }

        var released = movie.Status is "" or "Released" || (movie.Year > 0 && movie.Year < now.Year);
        return released ? null : DateTimeOffset.MaxValue;
    }

    /// <summary>The earlier of the digital and the physical release; null when neither is known.</summary>
    public static DateOnly? HomeRelease(Movie movie)
    {
        ArgumentNullException.ThrowIfNull(movie);
        DateOnly? earliest = null;
        foreach (var value in new[] { movie.DigitalRelease, movie.PhysicalRelease })
        {
            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && (earliest is null || date < earliest))
            {
                earliest = date;
            }
        }

        return earliest;
    }

    /// <summary>Availability with the current settings.</summary>
    public DateTimeOffset? AvailableFrom(Movie movie, DateTimeOffset now)
    {
        var values = Values;
        return AvailableFrom(movie, values.MinimumAvailability, values.AvailabilityDelay, now);
    }

    public bool IsAvailable(Movie movie, DateTimeOffset now) => AvailableFrom(movie, now) is null;

    /// <summary>"DKSUBS, nlsub" → ["DKSUBS", "nlsub"].</summary>
    public static IReadOnlyList<string> SplitTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    private int RssInterval()
    {
        var minutes = Int(ConfigKeys.IndexerRssSyncInterval, DefaultRssSyncInterval, 0, MaxRssSyncInterval);
        return minutes is > 0 and < MinRssSyncInterval ? MinRssSyncInterval : minutes;
    }

    private int MissingInterval()
    {
        var hours = Int(ConfigKeys.IndexerMissingSearchInterval, 0, 0, MaxMissingSearchInterval);
        return hours is > 0 and < MinMissingSearchInterval ? MinMissingSearchInterval : hours;
    }

    private int Int(string key, int fallback, int min, int max) =>
        int.TryParse(config.Get(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : fallback;

    private bool Bool(string key, bool fallback) => config.Get(key) switch
    {
        { } v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        { } v when v.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => fallback,
    };

    private void Set(string key, int value) => config.Set(key, value.ToString(CultureInfo.InvariantCulture));
}
