// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;

namespace StreamZH.Library.Migration.Arr.Settings;

/// <summary>
/// Lenient reading of Radarr/Sonarr API JSON: properties are camelCase (looked up case-insensitively as a fallback),
/// enums come as camelCase strings (Radarr, Sonarr v4) or numbers (some Sonarr fields), provider settings as a field list.
/// </summary>
public static class ArrJson
{
    /// <summary>The value the old apps send instead of a stored password or API key.</summary>
    public const string Masked = "********";

    public static JsonElement? Get(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    public static string? Str(JsonElement element, string name) => Text(Get(element, name));

    /// <summary>Text of a string or number value; null for null, missing or other kinds.</summary>
    public static string? Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    public static int? Int(JsonElement element, string name) => Int(Get(element, name));

    public static int? Int(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.Number } v when v.TryGetDouble(out var d) && d >= int.MinValue && d <= int.MaxValue => (int)Math.Round(d),
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    public static double? Double(JsonElement element, string name) => Get(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static bool Bool(JsonElement element, string name, bool fallback = false) => Get(element, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v when bool.TryParse(v.GetString(), out var b) => b,
        _ => fallback,
    };

    public static IReadOnlyList<JsonElement> Array(JsonElement element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

    /// <summary>
    /// A list of strings: a JSON array of strings, or a comma separated string (Radarr v4 restrictions, Sonarr v3 release
    /// profiles). Entries trimmed, empty ones dropped.
    /// </summary>
    public static IReadOnlyList<string> Strings(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Array } array =>
        [
            .. array.EnumerateArray().Select(v => Text(v)).OfType<string>().Select(s => s.Trim()).Where(s => s.Length > 0),
        ],
        { ValueKind: JsonValueKind.String } text =>
        [
            .. (text.GetString() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ],
        _ => [],
    };

    /// <summary>The ids of a list like "tags": [1, 4].</summary>
    public static IReadOnlyList<int> Ints(JsonElement element, string name) =>
        [.. Array(element, name).Select(v => Int(v)).OfType<int>()];

    /// <summary>
    /// An enum value as its index in <paramref name="names"/> (the enum's members in order): a number, or a name in any
    /// casing ("smart", "Smart"). Null when it is not one of them.
    /// </summary>
    public static int? Enum(JsonElement element, string name, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        switch (Get(element, name))
        {
            case { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var i):
                return i >= 0 && i < names.Length ? i : null;
            case { ValueKind: JsonValueKind.String } v:
                var text = v.GetString() ?? string.Empty;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return number >= 0 && number < names.Length ? number : null;
                }

                var index = System.Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
                return index >= 0 ? index : null;
            default:
                return null;
        }
    }

    /// <summary>The value of one setting of a provider resource ({"fields": [{"name": "host", "value": …}]}).</summary>
    public static JsonElement? Field(JsonElement provider, string name)
    {
        foreach (var field in Array(provider, "fields"))
        {
            if (Str(field, "name") is { } fieldName && fieldName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Get(field, "value");
            }
        }

        return null;
    }

    public static bool IsMasked(JsonElement? value) => value is { ValueKind: JsonValueKind.String } v && v.GetString() == Masked;
}
