// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamZH.Library.Migration.Sabnzbd;

/// <summary>One Usenet server of SABnzbd (config section "servers"). <see cref="Password"/> is masked ("**********") or empty.</summary>
public sealed record SabnzbdServer(
    string Name,
    string DisplayName,
    string Host,
    int Port,
    int Timeout,
    string Username,
    string Password,
    int Connections,
    bool Ssl,
    int SslVerify,
    bool Enable,
    bool Required,
    bool Optional,
    int PipeliningRequests,
    int Retention,
    string ExpireDate,
    string Quota,
    int Priority,
    string Notes)
{
    /// <summary>SABnzbd sends "*" × 10 for a stored password and "" for none.</summary>
    public bool HasMaskedPassword => Password.Length > 0 && Password.Trim('*').Length == 0;

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>One category of SABnzbd. "*" is the default category; empty <see cref="Pp"/> and priority -100 mean "like *".</summary>
public sealed record SabnzbdCategory(string Name, int Order, string Pp, string Script, string Dir, int Priority);

/// <summary>SABnzbd's configuration as mode=get_config returns it (SABnzbd 3.x and 4.x share the shape).</summary>
public sealed partial class SabnzbdConfig
{
    /// <summary>SABnzbd's DEFAULT_PRIORITY: the category takes the priority of "*".</summary>
    public const int DefaultPriority = -100;

    private readonly Dictionary<string, JsonElement> _misc;

    private SabnzbdConfig(Dictionary<string, JsonElement> misc, IReadOnlyList<SabnzbdServer> servers, IReadOnlyList<SabnzbdCategory> categories, int rssFeeds, int activeSorters)
    {
        _misc = misc;
        Servers = servers;
        Categories = categories;
        RssFeeds = rssFeeds;
        ActiveSorters = activeSorters;
    }

    public IReadOnlyList<SabnzbdServer> Servers { get; }

    public IReadOnlyList<SabnzbdCategory> Categories { get; }

    public int RssFeeds { get; }

    public int ActiveSorters { get; }

    public static SabnzbdConfig Parse(JsonElement config)
    {
        var misc = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (config.TryGetProperty("misc", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in m.EnumerateObject())
            {
                misc[property.Name] = property.Value.Clone();
            }
        }

        var servers = new List<SabnzbdServer>();
        foreach (var s in List(config, "servers"))
        {
            var name = Text(s, "name");
            if (name.Length == 0)
            {
                continue;
            }

            servers.Add(new SabnzbdServer(
                name,
                Text(s, "displayname"),
                Text(s, "host"),
                Int(s, "port") ?? 119,
                Int(s, "timeout") ?? 60,
                Text(s, "username"),
                Text(s, "password"),
                Int(s, "connections") ?? 1,
                Bool(s, "ssl") ?? false,
                Int(s, "ssl_verify") ?? 3,
                Bool(s, "enable") ?? true,
                Bool(s, "required") ?? false,
                Bool(s, "optional") ?? false,
                Int(s, "pipelining_requests") ?? 1,
                Int(s, "retention") ?? 0,
                Text(s, "expire_date"),
                Text(s, "quota"),
                Int(s, "priority") ?? 0,
                Text(s, "notes")));
        }

        var categories = List(config, "categories")
            .Select(c => new SabnzbdCategory(Text(c, "name"), Int(c, "order") ?? 0, Text(c, "pp"), Text(c, "script"), Text(c, "dir"), Int(c, "priority") ?? DefaultPriority))
            .Where(c => c.Name.Length > 0)
            .ToList();

        var rss = List(config, "rss").Count(r => Bool(r, "enable") ?? true);
        var sorters = List(config, "sorters").Count(r => Bool(r, "is_active") ?? false);
        return new SabnzbdConfig(misc, servers, categories, rss, sorters);
    }

    public string Misc(string key) => _misc.TryGetValue(key, out var value) ? AsText(value) : string.Empty;

    public int? MiscInt(string key) => _misc.TryGetValue(key, out var value) ? AsInt(value) : null;

    public bool? MiscBool(string key) => _misc.TryGetValue(key, out var value) ? AsBool(value) : null;

    public bool HasMisc(string key) => _misc.ContainsKey(key);

    /// <summary>A list setting ("cleanup_list", "unwanted_extensions", "schedlines"): a JSON list, or a comma separated text.</summary>
    public IReadOnlyList<string> MiscList(string key)
    {
        if (!_misc.TryGetValue(key, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return [.. value.EnumerateArray().Select(AsText).Where(t => t.Trim().Length > 0)];
        }

        return [.. AsText(value).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// SABnzbd's K/M/G/T notation ("50M", "1.5G", "500K", "1000") as bytes, 1024 based like SABnzbd's from_units; null when empty or unreadable.
    /// </summary>
    public static double? FromUnits(string? value)
    {
        var text = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        var match = UnitsRegex().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups[2].Value;
        var power = unit.Length == 0 ? 0 : "KMGTP".IndexOf(unit[0], StringComparison.Ordinal) + 1;
        return number * Math.Pow(1024, power);
    }

    /// <summary>A Linux ("/media/tv") or Windows ("D:\Downloads", "\\server\share") absolute path; SABnzbd also allows paths relative to its home folder.</summary>
    public static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static IEnumerable<JsonElement> List(JsonElement config, string section) =>
        config.TryGetProperty(section, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? AsText(value) : string.Empty;

    private static int? Int(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? AsInt(value) : null;

    private static bool? Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? AsBool(value) : null;

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(AsText)),
        _ => string.Empty,
    };

    private static int? AsInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var n) => n,
        JsonValueKind.Number when value.TryGetDouble(out var d) => (int)Math.Round(d),
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    // SABnzbd writes booleans as 0/1 (3.x/4.x) or true/false (newer); both are read.
    private static bool? AsBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var n) => n != 0,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" or "" => false,
            _ => null,
        },
        _ => null,
    };

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([KMGTP]?)B?\s*$")]
    private static partial Regex UnitsRegex();
}
