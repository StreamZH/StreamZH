// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;

namespace StreamZH.Core.CustomFormats;

/// <summary>
/// Reads the field values of a condition leniently: numbers may come as JSON numbers or strings ("4"), booleans as
/// true/false, "true"/"false" or 0/1, like the JSON Radarr, Sonarr and the TRaSH Guides produce.
/// </summary>
public static class CustomFormatFields
{
    public const string Value = "value";
    public const string Min = "min";
    public const string Max = "max";
    public const string ExceptLanguage = "exceptLanguage";

    public static bool Has(CustomFormatSpecification specification, string name)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return specification.Fields.TryGetValue(name, out var element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public static string? String(CustomFormatSpecification specification, string name)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!specification.Fields.TryGetValue(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static double? Double(CustomFormatSpecification specification, string name)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!specification.Fields.TryGetValue(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static int? Int(CustomFormatSpecification specification, string name)
    {
        var value = Double(specification, name);
        return value is { } d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;
    }

    public static bool Bool(CustomFormatSpecification specification, string name)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!specification.Fields.TryGetValue(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
            JsonValueKind.Number => element.TryGetDouble(out var d) && d != 0,
            _ => false,
        };
    }

    public static JsonElement Number(double value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Number(int value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Boolean(bool value) => JsonSerializer.SerializeToElement(value);
}
