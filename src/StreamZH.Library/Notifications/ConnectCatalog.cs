// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core;
using StreamZH.Data;

namespace StreamZH.Library.Notifications;

/// <summary>Input types of connection fields; the UI draws the matching control.</summary>
public static class ConnectFieldTypes
{
    public const string Text = "text";
    public const string Url = "url";

    /// <summary>A secret: masked in the API ("********"), sending the mask back keeps the stored value.</summary>
    public const string Password = "password";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Select = "select";
    public const string MultiSelect = "multiSelect";

    /// <summary>A comma separated list (e.g. topics, devices, recipients), stored as a string.</summary>
    public const string List = "list";
    public const string TextArea = "textArea";
}

/// <summary>Trigger names as the API and the UI use them.</summary>
public static class ConnectTriggers
{
    public const string Grab = "grab";
    public const string Import = "import";
    public const string Upgrade = "upgrade";
    public const string Rename = "rename";
    public const string DownloadFailure = "downloadFailure";
    public const string HealthIssue = "healthIssue";
    public const string HealthRestored = "healthRestored";

    // Requests (0.10): notification services only, all off by default.
    public const string RequestCreated = "requestCreated";
    public const string RequestApproved = "requestApproved";
    public const string RequestDeclined = "requestDeclined";
    public const string RequestAvailable = "requestAvailable";

    public static readonly IReadOnlyList<string> Requests = [RequestCreated, RequestApproved, RequestDeclined, RequestAvailable];

    public static readonly IReadOnlyList<string> All = [Grab, Import, Upgrade, Rename, DownloadFailure, HealthIssue, HealthRestored, .. Requests];

    public static string? For(ConnectEventType type) => type switch
    {
        ConnectEventType.Grab => Grab,
        ConnectEventType.Import => Import,
        ConnectEventType.Upgrade => Upgrade,
        ConnectEventType.Rename => Rename,
        ConnectEventType.DownloadFailure => DownloadFailure,
        ConnectEventType.HealthIssue => HealthIssue,
        ConnectEventType.HealthRestored => HealthRestored,
        ConnectEventType.RequestCreated => RequestCreated,
        ConnectEventType.RequestApproved => RequestApproved,
        ConnectEventType.RequestDeclined => RequestDeclined,
        ConnectEventType.RequestAvailable => RequestAvailable,
        _ => null,
    };

    public static bool IsOn(ConnectionTriggers triggers, string trigger)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        return trigger switch
        {
            Grab => triggers.OnGrab,
            Import => triggers.OnImport,
            Upgrade => triggers.OnUpgrade,
            Rename => triggers.OnRename,
            DownloadFailure => triggers.OnDownloadFailure,
            HealthIssue => triggers.OnHealthIssue,
            HealthRestored => triggers.OnHealthRestored,
            RequestCreated => triggers.OnRequestCreated,
            RequestApproved => triggers.OnRequestApproved,
            RequestDeclined => triggers.OnRequestDeclined,
            RequestAvailable => triggers.OnRequestAvailable,
            _ => false,
        };
    }
}

public sealed record ConnectFieldOption(string Value, string Label);

/// <summary>One setting of a connection type.</summary>
/// <param name="Name">Key in the settings (camelCase); also the key of its validation error.</param>
/// <param name="Default">bool, int, string or string[] depending on <see cref="Type"/>; null = empty.</param>
/// <param name="Advanced">Only shown with the Advanced switch on.</param>
/// <param name="Secret">
/// 0.12 (SV2 N-04): a free-text field that holds credentials (custom headers such as "Authorization: Bearer …"); treated
/// like a password field: masked in the API, encrypted at rest, kept only for the same destination.
/// </param>
public sealed record ConnectField(
    string Name,
    string Label,
    string Type,
    string? Help = null,
    object? Default = null,
    bool Required = false,
    bool Advanced = false,
    IReadOnlyList<ConnectFieldOption>? Options = null,
    int? Min = null,
    int? Max = null,
    string? Placeholder = null,
    string? Unit = null,
    bool Secret = false)
{
    /// <summary>A password field or a <see cref="Secret"/> text field.</summary>
    public bool IsSecret => Type == ConnectFieldTypes.Password || Secret;
}

/// <summary>A media server or notification service StreamZH can talk to.</summary>
/// <param name="Kind">"mediaServer" or "notification".</param>
/// <param name="Triggers">The triggers this type can react to (see <see cref="ConnectTriggers"/>).</param>
/// <param name="Defaults">Triggers that are on for a new connection of this type.</param>
public sealed record ConnectType(
    string Key,
    string Name,
    string Kind,
    string Description,
    string? InfoUrl,
    IReadOnlyList<string> Triggers,
    ConnectionTriggers Defaults,
    IReadOnlyList<ConnectField> Fields);

/// <summary>Every connection type with its fields (Radarr's option names where they fit).</summary>
public static class ConnectCatalog
{
    public const string MediaServer = "mediaServer";
    public const string Notification = "notification";

    public const string Plex = "plex";
    public const string Jellyfin = "jellyfin";
    public const string Emby = "emby";
    public const string Kodi = "kodi";
    public const string Discord = "discord";
    public const string Telegram = "telegram";
    public const string Pushover = "pushover";
    public const string Ntfy = "ntfy";
    public const string Gotify = "gotify";
    public const string Email = "email";
    public const string Webhook = "webhook";
    public const string Apprise = "apprise";

    /// <summary>Grab and import fields of Discord embeds.</summary>
    public static readonly IReadOnlyList<ConnectFieldOption> DiscordFields =
    [
        new("overview", "Overview"),
        new("genres", "Genres"),
        new("quality", "Quality"),
        new("group", "Release group"),
        new("size", "Size"),
        new("indexer", "Indexer"),
        new("release", "Release name"),
        new("links", "Links"),
        new("poster", "Poster"),
        new("fanart", "Fanart"),
    ];

    private static readonly ConnectionTriggers NotificationDefaults = new(OnGrab: true, OnImport: true, OnUpgrade: true, OnDownloadFailure: true);
    private static readonly ConnectionTriggers LibraryDefaults = new(OnImport: true, OnUpgrade: true, OnRename: true);

    private static readonly string[] LibraryTriggers = [ConnectTriggers.Import, ConnectTriggers.Upgrade, ConnectTriggers.Rename];

    private static readonly ConnectField MapFrom = new(
        "mapFrom",
        "Map paths from",
        ConnectFieldTypes.Text,
        "Start of the path as StreamZH sees it, e.g. /media. Only when the server sees your library under a different path.",
        Advanced: true,
        Placeholder: "/media");

    private static readonly ConnectField MapTo = new(
        "mapTo",
        "Map paths to",
        ConnectFieldTypes.Text,
        "The same folder as the server sees it, e.g. /data or D:\\Media.",
        Advanced: true,
        Placeholder: "/data");

    public static readonly IReadOnlyList<ConnectType> All =
    [
        new(
            Plex,
            "Plex Media Server",
            MediaServer,
            "Scans the movie or series folder in the matching Plex library after an import or rename.",
            "https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/",
            LibraryTriggers,
            LibraryDefaults,
            [
                new("host", "Host", ConnectFieldTypes.Text, "Address of the Plex server, without http://.", Required: true, Placeholder: "localhost"),
                new("port", "Port", ConnectFieldTypes.Number, Default: 32400, Required: true, Min: 1, Max: 65535),
                new("useSsl", "Use SSL", ConnectFieldTypes.Bool, "Connect with https.", Default: false),
                new(
                    "authToken",
                    "Auth token",
                    ConnectFieldTypes.Password,
                    "Your X-Plex-Token. In Plex Web open any item, choose ⋯ → Get Info → View XML; the token is at the end of the address.",
                    Required: true),
                new("updateLibrary", "Update library", ConnectFieldTypes.Bool, "Scan the folder of the movie or series after an import or rename.", Default: true),
                MapFrom,
                MapTo,
            ]),
        new(
            Jellyfin,
            "Jellyfin",
            MediaServer,
            "Tells Jellyfin which folder changed, so new files show up at once.",
            "https://jellyfin.org/docs/general/server/configuration/",
            LibraryTriggers,
            LibraryDefaults,
            [
                new("url", "URL", ConnectFieldTypes.Url, "Address of the server with port.", Required: true, Placeholder: "http://localhost:8096"),
                new("apiKey", "API key", ConnectFieldTypes.Password, "Dashboard → API Keys → +.", Required: true),
                new("updateLibrary", "Update library", ConnectFieldTypes.Bool, Default: true),
                new(
                    "updateMode",
                    "Update mode",
                    ConnectFieldTypes.Select,
                    "Folder: only the changed movie or series folder is scanned. Full: the whole library.",
                    "path",
                    Advanced: true,
                    Options: [new("path", "Changed folder"), new("full", "Full library scan")]),
                MapFrom,
                MapTo,
            ]),
        new(
            Emby,
            "Emby",
            MediaServer,
            "Tells Emby which folder changed and can show a notification to Emby admins.",
            "https://emby.media/support/articles/API-Key.html",
            [ConnectTriggers.Grab, ConnectTriggers.Import, ConnectTriggers.Upgrade, ConnectTriggers.Rename, ConnectTriggers.HealthIssue, ConnectTriggers.HealthRestored],
            LibraryDefaults,
            [
                new("url", "URL", ConnectFieldTypes.Url, "Address of the server with port.", Required: true, Placeholder: "http://localhost:8096"),
                new("apiKey", "API key", ConnectFieldTypes.Password, "Settings → Advanced → API Keys → New API Key.", Required: true),
                new("sendNotifications", "Send notifications", ConnectFieldTypes.Bool, "Show grabs, imports and health issues to Emby admins.", Default: false),
                new("updateLibrary", "Update library", ConnectFieldTypes.Bool, Default: true),
                new(
                    "updateMode",
                    "Update mode",
                    ConnectFieldTypes.Select,
                    "Folder: only the changed movie or series folder is scanned. Full: the whole library.",
                    "path",
                    Advanced: true,
                    Options: [new("path", "Changed folder"), new("full", "Full library scan")]),
                MapFrom,
                MapTo,
            ]),
        new(
            Kodi,
            "Kodi",
            MediaServer,
            "Updates the Kodi library over JSON-RPC and shows a notification on screen.",
            "https://kodi.wiki/view/Settings/Services/Control",
            [ConnectTriggers.Grab, ConnectTriggers.Import, ConnectTriggers.Upgrade, ConnectTriggers.Rename, ConnectTriggers.HealthIssue, ConnectTriggers.HealthRestored],
            new ConnectionTriggers(OnGrab: true, OnImport: true, OnUpgrade: true, OnRename: true),
            [
                new("host", "Host", ConnectFieldTypes.Text, "Address of the Kodi device. Turn on Settings → Services → Control → Allow remote control via HTTP.", Required: true, Placeholder: "192.168.1.20"),
                new("port", "Port", ConnectFieldTypes.Number, Default: 8080, Required: true, Min: 1, Max: 65535),
                new("useSsl", "Use SSL", ConnectFieldTypes.Bool, Default: false, Advanced: true),
                new("username", "User name", ConnectFieldTypes.Text, Default: "kodi"),
                new("password", "Password", ConnectFieldTypes.Password),
                new("notify", "GUI notification", ConnectFieldTypes.Bool, "Show grabs and imports on the Kodi screen.", Default: true),
                new("displayTime", "Display time", ConnectFieldTypes.Number, "How long the notification stays on screen.", 5, Advanced: true, Min: 1, Max: 60, Unit: "seconds"),
                new("updateLibrary", "Update library", ConnectFieldTypes.Bool, "Scan the folder after an import or rename.", Default: true),
                new("cleanLibrary", "Clean library", ConnectFieldTypes.Bool, "Remove entries of missing files after the update (e.g. replaced by an upgrade).", Default: false),
                new("alwaysUpdate", "Always update", ConnectFieldTypes.Bool, "Update even while a video is playing. Off: the update is skipped during playback.", Default: false, Advanced: true),
                MapFrom,
                MapTo,
            ]),
        new(
            Discord,
            "Discord",
            Notification,
            "Posts a message with poster and details to a Discord channel.",
            "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("webHookUrl", "Webhook URL", ConnectFieldTypes.Password, "Channel settings → Integrations → Webhooks → New Webhook → Copy Webhook URL.", Required: true, Placeholder: "https://discord.com/api/webhooks/…"),
                new("username", "User name", ConnectFieldTypes.Text, "Name the message is posted as.", "StreamZH"),
                new("avatar", "Avatar", ConnectFieldTypes.Url, "Picture URL for the poster of the message.", Advanced: true),
                new("grabFields", "On grab fields", ConnectFieldTypes.MultiSelect, "What a grab message shows.", (string[])["overview", "quality", "group", "size", "indexer", "links", "poster"], Advanced: true, Options: DiscordFields),
                new("importFields", "On import fields", ConnectFieldTypes.MultiSelect, "What an import or upgrade message shows.", (string[])["overview", "quality", "group", "size", "links", "poster"], Advanced: true, Options: DiscordFields),
            ]),
        new(
            Telegram,
            "Telegram",
            Notification,
            "Sends a message from your own Telegram bot.",
            "https://core.telegram.org/bots/features#botfather",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("botToken", "Bot token", ConnectFieldTypes.Password, "From @BotFather: /newbot.", Required: true),
                new("chatId", "Chat ID", ConnectFieldTypes.Text, "Your user, group or channel id. Send the bot a message first; @userinfobot shows your id.", Required: true, Placeholder: "123456789 or -100…"),
                new("topicId", "Topic ID", ConnectFieldTypes.Number, "Only for groups with topics: the thread to post in.", Advanced: true, Min: 1),
                new("sendSilently", "Send silently", ConnectFieldTypes.Bool, "The message arrives without a sound.", Default: false),
                new("includeLinks", "Include links", ConnectFieldTypes.Bool, "Add TMDB and IMDb links.", Default: true, Advanced: true),
            ]),
        new(
            Pushover,
            "Pushover",
            Notification,
            "Push notifications to your phones and desktops with Pushover.",
            "https://pushover.net/apps/build",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("apiKey", "API token", ConnectFieldTypes.Password, "Token of an application you create on pushover.net.", Required: true),
                new("userKey", "User key", ConnectFieldTypes.Password, "Your user or group key from the Pushover dashboard.", Required: true),
                new("devices", "Devices", ConnectFieldTypes.List, "Device names, separated by commas. Empty: all devices.", Advanced: true),
                new(
                    "priority",
                    "Priority",
                    ConnectFieldTypes.Select,
                    "Emergency repeats until you acknowledge it.",
                    "0",
                    Options: [new("-2", "Silent"), new("-1", "Quiet"), new("0", "Normal"), new("1", "High"), new("2", "Emergency")]),
                new("retry", "Retry", ConnectFieldTypes.Number, "Emergency only: repeat every … seconds (at least 30).", 60, Advanced: true, Min: 30, Max: 10800, Unit: "seconds"),
                new("expire", "Expire", ConnectFieldTypes.Number, "Emergency only: stop repeating after … seconds.", 3600, Advanced: true, Min: 30, Max: 10800, Unit: "seconds"),
                new("sound", "Sound", ConnectFieldTypes.Text, "Name of a Pushover sound, e.g. pushover or magic. Empty: your default.", Advanced: true),
            ]),
        new(
            Ntfy,
            "ntfy",
            Notification,
            "Push notifications through ntfy.sh or your own ntfy server.",
            "https://docs.ntfy.sh/publish/",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("serverUrl", "Server URL", ConnectFieldTypes.Url, "Leave as is for ntfy.sh.", "https://ntfy.sh", Required: true),
                new("topics", "Topics", ConnectFieldTypes.List, "One or more topics, separated by commas. Pick a hard-to-guess name on ntfy.sh.", Required: true),
                new("accessToken", "Access token", ConnectFieldTypes.Password, "For protected topics. Or use user name and password.", Advanced: true),
                new("username", "User name", ConnectFieldTypes.Text, Advanced: true),
                new("password", "Password", ConnectFieldTypes.Password, Advanced: true),
                new(
                    "priority",
                    "Priority",
                    ConnectFieldTypes.Select,
                    Default: "3",
                    Options: [new("1", "Min"), new("2", "Low"), new("3", "Default"), new("4", "High"), new("5", "Max")]),
                new("tags", "Tags", ConnectFieldTypes.List, "Tags or emoji short codes, separated by commas, e.g. movie_camera.", Advanced: true),
                new("clickUrl", "Click URL", ConnectFieldTypes.Url, "Opened when you tap the notification. Empty: the TMDB page.", Advanced: true),
                new("includePoster", "Include poster", ConnectFieldTypes.Bool, "Attach the poster from TMDB.", Default: true, Advanced: true),
            ]),
        new(
            Gotify,
            "Gotify",
            Notification,
            "Push notifications through your own Gotify server.",
            "https://gotify.net/docs/pushmsg",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("server", "Gotify server", ConnectFieldTypes.Url, Required: true, Placeholder: "https://gotify.example.com"),
                new("appToken", "App token", ConnectFieldTypes.Password, "Apps → Create application → token.", Required: true),
                new(
                    "priority",
                    "Priority",
                    ConnectFieldTypes.Select,
                    Default: "5",
                    Options: [new("0", "Min"), new("2", "Low"), new("5", "Normal"), new("8", "High")]),
                new("includePoster", "Include poster", ConnectFieldTypes.Bool, "Show the poster in the Gotify app.", Default: true, Advanced: true),
            ]),
        new(
            Email,
            "Email",
            Notification,
            "Sends an email through your mail server (SMTP).",
            null,
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("server", "Server", ConnectFieldTypes.Text, "SMTP server of your mail provider.", Required: true, Placeholder: "smtp.example.com"),
                new("port", "Port", ConnectFieldTypes.Number, "587 with STARTTLS, 465 with SSL, 25 without.", 587, Required: true, Min: 1, Max: 65535),
                new(
                    "useEncryption",
                    "Use encryption",
                    ConnectFieldTypes.Select,
                    "Preferred: encrypt when the server offers it. Always: refuse to send without (SSL on port 465, STARTTLS otherwise).",
                    "preferred",
                    Options: [new("preferred", "Preferred"), new("always", "Always"), new("never", "Never")]),
                new("username", "User name", ConnectFieldTypes.Text),
                new("password", "Password", ConnectFieldTypes.Password),
                new("from", "From address", ConnectFieldTypes.Text, Required: true, Placeholder: "streamzh@example.com"),
                new("to", "Recipients", ConnectFieldTypes.List, "Addresses, separated by commas.", Required: true),
                new("cc", "CC", ConnectFieldTypes.List, Advanced: true),
                new("bcc", "BCC", ConnectFieldTypes.List, Advanced: true),
            ]),
        new(
            Webhook,
            "Webhook",
            Notification,
            "Sends every event as JSON to your own URL (same layout as Radarr and Sonarr webhooks).",
            AppInfo.RepositoryUrl + "/blob/main/docs/WEBHOOK.md",
            ConnectTriggers.All,
            new ConnectionTriggers(OnGrab: true, OnImport: true, OnUpgrade: true, OnRename: true, OnDownloadFailure: true),
            [
                new("url", "URL", ConnectFieldTypes.Url, Required: true, Placeholder: "https://example.com/hook"),
                new("method", "Method", ConnectFieldTypes.Select, Default: "POST", Options: [new("POST", "POST"), new("PUT", "PUT")]),
                new("username", "User name", ConnectFieldTypes.Text, "For basic authentication.", Advanced: true),
                new("password", "Password", ConnectFieldTypes.Password, Advanced: true),
                new("headers", "Headers", ConnectFieldTypes.TextArea, "Extra headers, one per line: Name: value. Stored like a password.", Advanced: true, Placeholder: "X-Token: abc", Secret: true),
            ]),
        new(
            Apprise,
            "Apprise",
            Notification,
            "Sends through an Apprise API server to any of its 100+ services.",
            "https://github.com/caronc/apprise-api",
            ConnectTriggers.All,
            NotificationDefaults,
            [
                new("serverUrl", "Apprise server URL", ConnectFieldTypes.Url, Required: true, Placeholder: "http://localhost:8000"),
                new("configurationKey", "Configuration key", ConnectFieldTypes.Text, "Key of a configuration stored on the server. Or enter Apprise URLs below."),
                new("statelessUrls", "Apprise URLs", ConnectFieldTypes.Password, "One or more Apprise URLs, separated by commas or spaces, e.g. tgram://token/chat."),
                new(
                    "notificationType",
                    "Notification type",
                    ConnectFieldTypes.Select,
                    Default: "info",
                    Advanced: true,
                    Options: [new("info", "Info"), new("success", "Success"), new("warning", "Warning"), new("failure", "Failure")]),
                new("tags", "Tags", ConnectFieldTypes.List, "Only notify the services with these tags.", Advanced: true),
                new("username", "User name", ConnectFieldTypes.Text, "For basic authentication.", Advanced: true),
                new("password", "Password", ConnectFieldTypes.Password, Advanced: true),
            ]),
    ];

    public static ConnectType? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool Supports(ConnectType type, ConnectEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(type);
        return eventType == ConnectEventType.Test || (ConnectTriggers.For(eventType) is { } trigger && type.Triggers.Contains(trigger));
    }
}
