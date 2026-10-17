// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using StreamZH.Data;

namespace StreamZH.Host.General;

/// <summary>
/// Cached reads of the general and UI settings. Some of them are read on every request (authentication) or every
/// outgoing HTTP request (proxy), so they must not open the database each time. Only this class writes these keys.
/// </summary>
public sealed class SettingsCache(ConfigRepository config)
{
    private readonly ConcurrentDictionary<string, string?> _values = new(StringComparer.Ordinal);

    /// <summary>The stored value; null when it is not set or empty (= default).</summary>
    public string? Get(string key) => _values.GetOrAdd(key, k => config.Get(k) is { Length: > 0 } value ? value : null);

    public void Set(string key, string? value)
    {
        config.Set(key, value ?? string.Empty);
        _values[key] = string.IsNullOrEmpty(value) ? null : value;
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback) => bool.TryParse(Get(key), out var value) ? value : fallback;

    public T GetEnum<T>(string key, T fallback)
        where T : struct, Enum => Enum.TryParse<T>(Get(key), ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : fallback;

    public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");
}
