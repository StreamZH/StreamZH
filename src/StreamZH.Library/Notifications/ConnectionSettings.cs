// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;

namespace StreamZH.Library.Notifications;

/// <summary>
/// Typed access to the stored settings of one connection, plus the conversion from and to the API: values are checked
/// and converted against the field list of the type, unknown keys are dropped and secrets are masked.
/// </summary>
public sealed class ConnectionSettings(IReadOnlyDictionary<string, JsonElement> values)
{
    /// <summary>What the API shows instead of a stored secret; sending it back keeps the secret.</summary>
    public const string Mask = "********";

    public IReadOnlyDictionary<string, JsonElement> Values { get; } = values;

    /// <summary>Trimmed text, null when empty.</summary>
    public string? Text(string name) =>
        Values.TryGetValue(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { } s && s.Trim().Length > 0 ? s.Trim() : null,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    public bool Bool(string name) => Values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True;

    public int? Int(string name) =>
        Values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>A multi-select (array) or a comma separated list; entries trimmed, empty ones dropped.</summary>
    public IReadOnlyList<string> List(string name)
    {
        if (!Values.TryGetValue(name, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.Trim()).Where(v => v.Length > 0)],
            JsonValueKind.String => SplitList(value.GetString()),
            _ => [],
        };
    }

    public static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The settings for the API: every field of the type, secrets masked when set.</summary>
    public static Dictionary<string, object?> ToResource(ConnectType type, IReadOnlyDictionary<string, JsonElement> stored)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(stored);
        var settings = new ConnectionSettings(stored);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            result[field.Name] = field.Type switch
            {
                ConnectFieldTypes.Password => settings.Text(field.Name) is null ? string.Empty : Mask,
                ConnectFieldTypes.Bool => settings.Bool(field.Name),
                ConnectFieldTypes.Number => settings.Int(field.Name),
                ConnectFieldTypes.MultiSelect => settings.List(field.Name),
                _ => settings.Text(field.Name) ?? string.Empty,
            };
        }

        return result;
    }

    /// <summary>The settings of a new connection of this type: the defaults of all fields.</summary>
    public static Dictionary<string, JsonElement> Defaults(ConnectType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Normalize(type, null, null, []);
    }

    /// <summary>
    /// Checks and converts the settings from the API. Fields that were not sent keep the value in <paramref name="stored"/>
    /// (else their default), a masked secret keeps the stored one. Problems are added to <paramref name="errors"/> under the field name.
    /// </summary>
    public static Dictionary<string, JsonElement> Normalize(
        ConnectType type,
        IReadOnlyDictionary<string, JsonElement>? input,
        IReadOnlyDictionary<string, JsonElement>? stored,
        Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(errors);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            JsonElement? value;
            if (input is not null && input.TryGetValue(field.Name, out var given))
            {
                // Sent: a masked secret keeps the stored one, null clears the field.
                value = field.Type == ConnectFieldTypes.Password && given.ValueKind == JsonValueKind.String && given.GetString() == Mask
                    ? Stored(stored, field.Name)
                    : given.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                        ? Convert(field, null, errors)
                        : Convert(field, given, errors);
            }
            else
            {
                // Not sent: keep what is stored, else the default.
                value = Stored(stored, field.Name) ?? Default(field);
            }

            if (value is { } v)
            {
                result[field.Name] = v;
            }

            if (field.Required && !HasValue(value))
            {
                errors.TryAdd(field.Name, [$"{field.Label} is required."]);
            }
        }

        return result;
    }

    private static bool HasValue(JsonElement? value) => value is { } v && v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!.Trim().Length > 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    };

    private static JsonElement? Stored(IReadOnlyDictionary<string, JsonElement>? stored, string name) =>
        stored is not null && stored.TryGetValue(name, out var value) ? value : null;

    private static JsonElement? Default(ConnectField field) => field.Default is null ? null : JsonSerializer.SerializeToElement(field.Default);

    private static JsonElement? Convert(ConnectField field, JsonElement? raw, Dictionary<string, string[]> errors)
    {
        if (raw is not { } value)
        {
            // Cleared: booleans are off, everything else empty.
            return field.Type == ConnectFieldTypes.Bool ? JsonSerializer.SerializeToElement(false) : null;
        }

        switch (field.Type)
        {
            case ConnectFieldTypes.Bool:
                return value.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False => value,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => JsonSerializer.SerializeToElement(b),
                    _ => Invalid(field, errors, "Use true or false."),
                };

            case ConnectFieldTypes.Number:
            {
                int? number = value.ValueKind switch
                {
                    JsonValueKind.Number when value.TryGetInt32(out var n) => n,
                    JsonValueKind.String when value.GetString()!.Trim().Length == 0 => null,
                    JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                    _ => int.MinValue,
                };
                if (number is null)
                {
                    return null;
                }

                if (number == int.MinValue || number < field.Min || number > field.Max)
                {
                    return Invalid(field, errors, Range(field));
                }

                return JsonSerializer.SerializeToElement(number.Value);
            }

            case ConnectFieldTypes.MultiSelect:
            {
                var items = value.ValueKind switch
                {
                    JsonValueKind.Array => value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToList(),
                    JsonValueKind.String => [.. SplitList(value.GetString())],
                    _ => null,
                };
                if (items is null || items.Any(i => i is null || field.Options?.Any(o => o.Value == i) != true))
                {
                    return Invalid(field, errors, $"Use any of: {string.Join(", ", field.Options?.Select(o => o.Value) ?? [])}.");
                }

                return JsonSerializer.SerializeToElement(items.Distinct().ToArray());
            }

            default:
            {
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()!.Trim(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
                if (text is null)
                {
                    return Invalid(field, errors, "Enter text.");
                }

                if (text.Length == 0)
                {
                    return null;
                }

                if (field.Type == ConnectFieldTypes.Select && field.Options?.Any(o => o.Value == text) != true)
                {
                    return Invalid(field, errors, $"Use one of: {string.Join(", ", field.Options?.Select(o => o.Value) ?? [])}.");
                }

                if (field.Type == ConnectFieldTypes.Url
                    && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                {
                    return Invalid(field, errors, "Enter a full URL starting with http:// or https://.");
                }

                if (text.Length > 4000)
                {
                    return Invalid(field, errors, "Too long.");
                }

                return JsonSerializer.SerializeToElement(text);
            }
        }
    }

    private static string Range(ConnectField field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => $"Use {min} to {max}.",
        ({ } min, null) => $"Use {min} or more.",
        (null, { } max) => $"Use up to {max}.",
        _ => "Enter a whole number.",
    };

    private static JsonElement? Invalid(ConnectField field, Dictionary<string, string[]> errors, string message)
    {
        errors.TryAdd(field.Name, [message]);
        return null;
    }
}
