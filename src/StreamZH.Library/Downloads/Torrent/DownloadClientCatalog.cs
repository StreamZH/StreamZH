// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Downloads;

namespace StreamZH.Library.Downloads.Torrent;

/// <summary>Kinds of setting fields, as the UI renders them.</summary>
public static class DownloadClientFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Checkbox = "checkbox";
    public const string Password = "password";
}

/// <summary>
/// One setting of a download client implementation (Radarr's FieldDefinition): stored under <see cref="Name"/> in
/// <see cref="DownloadClientDefinition.Settings"/>. <see cref="Secret"/> values are never returned by the API.
/// </summary>
public sealed record DownloadClientField(
    string Name,
    string Label,
    string Type,
    JsonElement Default,
    string? HelpText = null,
    bool Advanced = false,
    bool Required = false,
    bool Secret = false,
    string? Placeholder = null)
{
    public bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false,
    };
}

/// <summary>An implementation the user can add (qBittorrent, Transmission, Deluge, the built-in engine) with its fields.</summary>
public sealed record DownloadClientImplementation(
    string Implementation,
    string Name,
    DownloadProtocol Protocol,
    string Description,
    string? InfoLink,
    IReadOnlyList<DownloadClientField> Fields)
{
    /// <summary>External clients are reached over the network (host, port, …); the built-in engine is not.</summary>
    public bool IsExternal => Fields.Any(f => f.Name == DownloadClientCatalog.Host);

    public DownloadClientField? Field(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The download client implementations and their settings, modelled on Radarr's download client settings.</summary>
public static class DownloadClientCatalog
{
    public const string Host = "host";
    public const string Port = "port";
    public const string UseSsl = "useSsl";
    public const string UrlBase = "urlBase";
    public const string Username = "username";
    public const string Password = "password";
    public const string Category = "category";
    public const string TvCategory = "tvCategory";
    public const string Directory = "directory";
    public const string AddPaused = "addPaused";

    public const string DefaultCategory = "streamzh";

    /// <summary>Shown instead of a stored secret; sending it back keeps the stored value.</summary>
    public const string Masked = "********";

    public static IReadOnlyList<DownloadClientImplementation> All { get; } =
    [
        new(
            DownloadClientDefinition.QBittorrent,
            "qBittorrent",
            DownloadProtocol.Torrent,
            "qBittorrent 4.1 or newer (also 5.x) through its Web UI. Turn on the Web UI in qBittorrent's options.",
            "https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-4.1)",
            [
                .. Connection(8080, null),
                Text(Username, "Username", string.Empty, "Web UI user name. Leave empty when qBittorrent skips the login for this address."),
                Secret(Password, "Password", "Web UI password."),
                .. Common("qBittorrent category. StreamZH creates it when it is missing."),
            ]),
        new(
            DownloadClientDefinition.Transmission,
            "Transmission",
            DownloadProtocol.Torrent,
            "Transmission 2.80 or newer through its RPC interface (labels need 3.00 or newer).",
            "https://github.com/transmission/transmission/blob/main/docs/rpc-spec.md",
            [
                .. Connection(9091, "/transmission/"),
                Text(Username, "Username", string.Empty, "RPC user name, when authentication is required."),
                Secret(Password, "Password", "RPC password."),
                .. Common("Label added to StreamZH's torrents (Transmission 3.00 or newer)."),
            ]),
        new(
            DownloadClientDefinition.Deluge,
            "Deluge",
            DownloadProtocol.Torrent,
            "Deluge 2 (or 1.3) through its Web UI. The Web UI must be connected to the daemon; enable the Label plugin for categories.",
            "https://deluge.readthedocs.io/en/latest/reference/webapi.html",
            [
                .. Connection(8112, null),
                Secret(Password, "Password", "Web UI password (Deluge's default is \"deluge\")."),
                .. Common("Label of StreamZH's torrents (needs Deluge's Label plugin). Lower case letters, digits, '-' and '_' only."),
            ]),
        new(
            DownloadClientDefinition.BuiltInTorrent,
            "StreamZH (built in)",
            DownloadProtocol.Torrent,
            "StreamZH's own torrent engine. No other program needed; its settings are in Settings → Download clients.",
            null,
            []),
    ];

    public static DownloadClientImplementation? Find(string? implementation) =>
        All.FirstOrDefault(i => string.Equals(i.Implementation, implementation, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default values of an implementation's fields.</summary>
    public static Dictionary<string, JsonElement> Defaults(DownloadClientImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        return implementation.Fields.ToDictionary(f => f.Name, f => f.Default, StringComparer.Ordinal);
    }

    private static IEnumerable<DownloadClientField> Connection(int port, string? urlBase) =>
    [
        Text(Host, "Host", "localhost", "Name or IP address of the computer the client runs on, without http://.", required: true),
        new(Port, "Port", DownloadClientFieldTypes.Number, JsonSerializer.SerializeToElement(port), "Port of the client's web interface.", Required: true),
        Check(UseSsl, "Use SSL", false, "Connect with HTTPS. The client must have HTTPS turned on."),
        Text(UrlBase, "URL base", urlBase ?? string.Empty, "Path in front of the API, e.g. behind a reverse proxy at /qbittorrent.", advanced: true),
    ];

    private static IEnumerable<DownloadClientField> Common(string categoryHelp) =>
    [
        Text(Category, "Category", DefaultCategory, $"{categoryHelp} Keeps StreamZH's torrents apart from your own."),
        Text(TvCategory, "Series category", string.Empty, "A separate category for series. Empty = the same as for movies.", advanced: true),
        Text(Directory, "Directory", string.Empty, "Where the client saves StreamZH's torrents. Empty = the client's default folder.", advanced: true),
        Check(AddPaused, "Add paused", false, "Add torrents stopped; start them in the client yourself.", advanced: true),
    ];

    private static DownloadClientField Text(string name, string label, string value, string help, bool advanced = false, bool required = false) =>
        new(name, label, DownloadClientFieldTypes.Text, JsonSerializer.SerializeToElement(value), help, advanced, required);

    private static DownloadClientField Secret(string name, string label, string help) =>
        new(name, label, DownloadClientFieldTypes.Password, JsonSerializer.SerializeToElement(string.Empty), help, Secret: true);

    private static DownloadClientField Check(string name, string label, bool value, string help, bool advanced = false) =>
        new(name, label, DownloadClientFieldTypes.Checkbox, JsonSerializer.SerializeToElement(value), help, advanced);
}
