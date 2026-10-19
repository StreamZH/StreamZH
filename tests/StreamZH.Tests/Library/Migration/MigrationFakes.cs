// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using System.Web;

namespace StreamZH.Tests.Library.Migration;

/// <summary>
/// SABnzbd 4.x answering /api like sabnzbd/api.py: mode=version without a key, everything else with the API key;
/// a wrong key gives {"status": false, "error": "API Key Incorrect"} (HTTP 200). get_config is shaped like
/// config.get_dconfig: passwords masked as ten stars, booleans as 0/1, categories ordered with "*" first.
/// </summary>
public sealed class StubSabnzbd : HttpMessageHandler
{
    public const string ApiKey = "0123456789abcdef0123456789abcdef";

    /// <summary>The paths of this fake; tests set <see cref="Root"/> to a temp folder and get_config uses it.</summary>
    public string Root { get; set; } = "/data";

    /// <summary>"" serves /api and /sabnzbd/api; "/sabnzbd" serves only /sabnzbd/api (an install behind a URL base).</summary>
    public string Base { get; set; } = string.Empty;

    /// <summary>Newer SABnzbd: a wrong key gets 403 with the text instead of the JSON error.</summary>
    public bool ForbiddenOnWrongKey { get; set; }

    public string Version { get; set; } = "4.3.3";

    public List<string> Modes { get; } = [];

    public Func<string, string>? ConfigOverride { get; set; }

    public string Config => """
        {
          "config": {
            "misc": {
              "helpful_warnings": 1, "queue_complete": "", "queue_complete_pers": 0, "bandwidth_perc": 80, "refresh_rate": 1,
              "interface_settings": "", "queue_limit": 20, "config_lock": 0, "fixed_ports": 1, "notified_new_skin": 2,
              "direct_unpack_tested": 1, "sorters_converted": 1, "check_new_rel": 1, "auto_browser": 0, "language": "en",
              "enable_https_verification": 1, "host": "::", "port": "8080", "https_port": "", "username": "", "password": "",
              "bandwidth_max": "50M", "cache_limit": "1G", "web_dir": "Glitter", "web_color": "Auto", "https_cert": "server.cert",
              "https_key": "server.key", "https_chain": "", "enable_https": 0, "inet_exposure": 0, "api_key": "0123456789abcdef0123456789abcdef",
              "nzb_key": "fedcba9876543210fedcba9876543210", "socks5_proxy_url": "", "permissions": "", "download_dir": "{{ROOT}}/downloads/incomplete",
              "download_free": "5G", "complete_dir": "{{ROOT}}/downloads/complete", "complete_free": "", "fulldisk_autoresume": 0, "script_dir": "",
              "nzb_backup_dir": "", "admin_dir": "admin", "backup_dir": "", "dirscan_dir": "{{ROOT}}/watch", "dirscan_speed": 5,
              "password_file": "", "log_dir": "logs", "max_art_tries": 3, "top_only": 0, "sfv_check": 1, "script_can_fail": 0,
              "enable_recursive": 1, "flat_unpack": 0, "par_option": "", "pre_check": 0, "nice": "", "win_process_prio": 3,
              "ionice": "", "fail_hopeless_jobs": 1, "fast_fail": 1, "auto_disconnect": 1, "pre_script": "None", "end_queue_script": "None",
              "no_dupes": 2, "no_series_dupes": 0, "no_smart_dupes": 0, "dupes_propercheck": 1, "pause_on_pwrar": 1,
              "ignore_samples": 0, "deobfuscate_final_filenames": 1, "auto_sort": "", "direct_unpack": 1, "propagation_delay": 0,
              "folder_rename": 1, "replace_spaces": 0, "replace_underscores": 0, "replace_dots": 0, "safe_postproc": 1,
              "pause_on_post_processing": 0, "enable_all_par": 0, "sanitize_safe": 0, "cleanup_list": [".nfo", ".sfv", "srr"],
              "unwanted_extensions": ["exe", "com", "bat", "sh"], "action_on_unwanted_extensions": 2, "unwanted_extensions_mode": 0,
              "new_nzb_on_failure": 0, "history_retention": "", "history_retention_option": "all", "history_retention_number": 1,
              "quota_size": "", "quota_day": "", "quota_resume": 0, "quota_period": "m", "enable_tv_sorting": 0, "enable_movie_sorting": 0,
              "enable_date_sorting": 0, "schedlines": [
                "1 0 1 1234567 pause", "1 0 7 1234567 resume", "1 30 23 67 speedlimit 50", "1 0 8 12345 speedlimit 2M",
                "1 0 2 12345 disable_server block.example.net", "1 0 6 12345 enable_server block.example.net",
                "1 15 4 1234567 pause_cat tv", "0 0 3 1 rss_scan", "1 nonsense"
              ],
              "rss_rate": 60, "ampm": 0, "start_paused": 0, "preserve_paused_state": 0, "enable_par_cleanup": 1, "process_unpacked_par2": 1,
              "enable_multipar": 1, "enable_unrar": 1, "enable_7zip": 1, "enable_filejoin": 1, "enable_tsjoin": 1, "overwrite_files": 0,
              "ignore_unrar_dates": 0, "backup_for_duplicates": 0, "empty_postproc": 0, "wait_for_dfolder": 0, "rss_filenames": 0,
              "api_logging": 1, "html_login": 1, "warn_dupl_jobs": 0, "keep_awake": 1, "tray_icon": 1, "allow_incomplete_nzb": 0,
              "enable_broadcast": 1, "ipv6_hosting": 0, "api_warnings": 1, "no_penalties": 0, "x_frame_options": 1,
              "allow_old_ssl_tls": 0, "enable_season_sorting": 1, "verify_xff_header": 0, "rss_odd_titles": ["nzbindex.nl/", "nzbindex.com/", "nzbclub.com/"],
              "quick_check_ext_ignore": ["nfo", "sfv", "srr"], "req_completion_rate": 100.2, "selftest_host": "self-test.sabnzbd.org",
              "movie_rename_limit": "100M", "episode_rename_limit": "20M", "size_limit": "0", "direct_unpack_threads": 3,
              "history_limit": 10, "wait_ext_drive": 5, "max_foldername_length": 246, "nomedia_marker": "", "ipv6_servers": 1,
              "url_base": "/sabnzbd", "host_whitelist": [], "local_ranges": [], "max_url_retries": 10, "downloader_sleep_time": 10,
              "receive_threads": 2, "switchinterval": 0.005, "ssdp_broadcast_interval": 15, "ext_rename_ignore": [],
              "email_server": "", "email_to": [], "email_from": "", "email_account": "", "email_pwd": "", "email_endjob": 0,
              "email_full": 0, "email_dir": "", "email_rss": 0, "email_cats": ["*"], "config_conversion_version": 4,
              "disable_par2cmdline": 0, "disable_key": 0, "unrar_parameters": ""
            },
            "logging": { "log_level": 1, "max_log_size": 5242880, "log_backups": 5 },
            "ncenter": { "ncenter_enable": 0, "ncenter_cats": ["*"] },
            "servers": [
              {
                "name": "news.example.com", "displayname": "Example News", "host": "news.example.com", "port": 563, "timeout": 60,
                "username": "johndoe", "password": "**********", "connections": 30, "ssl": 1, "ssl_verify": 3, "ssl_ciphers": "",
                "enable": 1, "required": 0, "optional": 0, "retention": 4000, "expire_date": "", "quota": "", "usage_at_start": 0,
                "priority": 0, "notes": "Main account", "pipelining_requests": 2
              },
              {
                "name": "block.example.net", "displayname": "Block account", "host": "block.example.net", "port": 443, "timeout": 120,
                "username": "jdoe-block", "password": "**********", "connections": 8, "ssl": 1, "ssl_verify": 1, "ssl_ciphers": "",
                "enable": 1, "required": 0, "optional": 1, "retention": 0, "expire_date": "2027-06-30", "quota": "500G", "usage_at_start": 0,
                "priority": 1, "notes": "", "pipelining_requests": 1
              },
              {
                "name": "free.example.org", "displayname": "", "host": "free.example.org", "port": 119, "timeout": 60,
                "username": "", "password": "", "connections": 2, "ssl": 0, "ssl_verify": 0, "ssl_ciphers": "",
                "enable": 0, "required": 0, "optional": 0, "retention": 0, "expire_date": "", "quota": "", "usage_at_start": 0,
                "priority": 2, "notes": "", "pipelining_requests": 1
              }
            ],
            "categories": [
              { "name": "*", "order": 0, "pp": "3", "script": "None", "dir": "", "newzbin": "", "priority": 0 },
              { "name": "movies", "order": 1, "pp": "", "script": "Default", "dir": "", "newzbin": "", "priority": -100 },
              { "name": "tv", "order": 2, "pp": "", "script": "Default", "dir": "tv", "newzbin": "", "priority": -100 },
              { "name": "anime", "order": 3, "pp": "2", "script": "notify.py", "dir": "{{ROOT}}/anime", "newzbin": "", "priority": 1 },
              { "name": "audio", "order": 4, "pp": "1", "script": "Default", "dir": "/nowhere/audio", "newzbin": "", "priority": -2 }
            ],
            "rss": [ { "name": "My feed", "uri": ["https://feed.example/rss"], "cat": "", "pp": "", "script": "", "enable": 1, "priority": -100, "filters": [] } ],
            "sorters": [],
            "indexers": []
          }
        }
        """.Replace("{{ROOT}}", Root, StringComparison.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.TrimEnd('/');
        var served = Base.Length == 0 ? path is "/api" or "/sabnzbd/api" : path == Base + "/api";
        if (!served)
        {
            return Task.FromResult(Text(HttpStatusCode.NotFound, "<html><body>Not found</body></html>", "text/html"));
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var mode = query["mode"] ?? string.Empty;
        Modes.Add(mode);
        if (mode == "version")
        {
            return Task.FromResult(Json($$"""{"version": "{{Version}}"}"""));
        }

        if (query["apikey"] != ApiKey)
        {
            var message = string.IsNullOrEmpty(query["apikey"]) ? "API Key Required" : "API Key Incorrect";
            return Task.FromResult(ForbiddenOnWrongKey ? Text(HttpStatusCode.Forbidden, message, "text/plain") : Json($$"""{"status": false, "error": "{{message}}"}"""));
        }

        if (mode == "get_config")
        {
            var config = ConfigOverride?.Invoke(Config) ?? Config;
            if (query["section"] == "misc" && query["keyword"] is { } keyword)
            {
                return Task.FromResult(Json("{\"config\": {\"misc\": {\"" + keyword + "\": \"x\"}}}"));
            }

            return Task.FromResult(Json(config));
        }

        return Task.FromResult(Json("""{"status": false, "error": "not implemented"}"""));
    }

    private static HttpResponseMessage Json(string body) => Text(HttpStatusCode.OK, body, "application/json");

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string type) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
}

/// <summary>
/// Radarr v5 / Sonarr v4 /api/v3 with X-Api-Key: /system/status, /indexer and /downloadclient shaped like their
/// IndexerResource and DownloadClientResource (ProviderResource fields, keys masked as "********").
/// </summary>
public sealed class StubArr(string app) : HttpMessageHandler
{
    public const string ApiKey = "arr-key-0123456789";

    public string App { get; } = app;

    public string? IndexerJson { get; set; }

    /// <summary>0.11: /downloadclient and /remotepathmapping answers (null = the defaults).</summary>
    public string? DownloadClientJson { get; set; }

    public string RemotePathMappingJson { get; set; } = "[]";

    public static string RadarrIndexers => """
        [
          {
            "enableRss": true, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "supportsRss": true, "supportsSearch": true,
            "protocol": "usenet", "priority": 10, "downloadClientId": 0, "name": "NZBgeek",
            "fields": [
              { "order": 0, "name": "baseUrl", "label": "URL", "value": "https://api.nzbgeek.info", "type": "textbox", "advanced": false, "privacy": "normal", "isFloat": false },
              { "order": 1, "name": "apiPath", "label": "API Path", "helpText": "Path to the api, usually /api", "value": "/api", "type": "textbox", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 2, "name": "apiKey", "label": "API Key", "value": "********", "type": "textbox", "advanced": false, "privacy": "apiKey", "isFloat": false },
              { "order": 3, "name": "categories", "label": "Categories", "value": [2030, 2040, 2045], "type": "select", "advanced": false, "selectOptionsProviderAction": "newznabCategories", "privacy": "normal", "isFloat": false },
              { "order": 4, "name": "additionalParameters", "label": "Additional Parameters", "type": "textbox", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 5, "name": "multiLanguages", "label": "Multi Languages", "value": [], "type": "select", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 6, "name": "failDownloads", "label": "Fail Downloads", "value": [], "type": "select", "advanced": true, "privacy": "normal", "isFloat": false },
              { "order": 7, "name": "removeYear", "label": "Remove year from search string", "value": false, "type": "checkbox", "advanced": true, "privacy": "normal", "isFloat": false }
            ],
            "implementationName": "Newznab", "implementation": "Newznab", "configContract": "NewznabSettings",
            "infoLink": "https://wiki.servarr.com/radarr/supported#newznab", "tags": [], "id": 1
          },
          {
            "enableRss": true, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "supportsRss": true, "supportsSearch": true,
            "protocol": "usenet", "priority": 25, "downloadClientId": 0, "name": "My Indexer",
            "fields": [
              { "order": 0, "name": "baseUrl", "value": "https://indexer.example.org/" },
              { "order": 1, "name": "apiPath", "value": "/api" },
              { "order": 2, "name": "apiKey", "value": "********", "privacy": "apiKey" },
              { "order": 3, "name": "categories", "value": [2000, 2040] },
              { "order": 4, "name": "additionalParameters", "value": "&extended=1" }
            ],
            "implementation": "Newznab", "tags": [], "id": 2
          },
          {
            "enableRss": true, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "protocol": "usenet", "priority": 25,
            "name": "DrunkenSlug (Prowlarr)",
            "fields": [
              { "order": 0, "name": "baseUrl", "value": "http://prowlarr:9696/3/" },
              { "order": 1, "name": "apiPath", "value": "/api" },
              { "order": 2, "name": "apiKey", "value": "********" },
              { "order": 3, "name": "categories", "value": [2000] }
            ],
            "implementation": "Newznab", "tags": [], "id": 3
          },
          {
            "enableRss": true, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "protocol": "torrent", "priority": 25,
            "name": "Some Torznab",
            "fields": [
              { "name": "baseUrl", "value": "https://torznab.example/api/v2.0/indexers/tracker/results/torznab/" }, { "name": "apiPath", "value": "/api" },
              { "name": "apiKey", "value": "********" }, { "name": "categories", "value": [2000, 2040] }, { "name": "minimumSeeders", "value": 3 },
              { "name": "seedCriteria.seedRatio", "value": 1.5 }, { "name": "seedCriteria.seedTime", "value": 4320 }, { "name": "seedCriteria.seasonPackSeedTime" },
              { "name": "rejectBlocklistedTorrentHashesWhileGrabbing", "value": false }
            ],
            "implementation": "Torznab", "tags": [], "id": 4
          },
          {
            "enableRss": false, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "protocol": "usenet", "priority": 30,
            "name": "NZBgeek again",
            "fields": [ { "name": "baseUrl", "value": "https://API.nzbgeek.info/" }, { "name": "apiPath", "value": "/api" }, { "name": "apiKey", "value": "********" }, { "name": "categories", "value": [2000] } ],
            "implementation": "Newznab", "tags": [], "id": 5
          },
          {
            "enableRss": true, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "protocol": "usenet", "priority": 25,
            "name": "omgwtfnzbs", "fields": [ { "name": "username", "value": "johndoe" }, { "name": "apiKey", "value": "********" } ],
            "implementation": "Omgwtfnzbs", "tags": [], "id": 6
          }
        ]
        """;

    public static string SonarrIndexers => """
        [
          {
            "enableRss": false, "enableAutomaticSearch": true, "enableInteractiveSearch": true, "supportsRss": true, "supportsSearch": true,
            "protocol": "usenet", "priority": 12, "seasonSearchMaximumSingleEpisodeAge": 0, "downloadClientId": 0, "name": "NZBgeek",
            "fields": [
              { "order": 0, "name": "baseUrl", "label": "URL", "value": "https://api.nzbgeek.info/", "type": "textbox" },
              { "order": 1, "name": "apiPath", "label": "API Path", "value": "/api", "type": "textbox", "advanced": true },
              { "order": 2, "name": "apiKey", "label": "API Key", "value": "********", "type": "textbox", "privacy": "apiKey" },
              { "order": 3, "name": "categories", "label": "Categories", "value": [5030, 5040], "type": "select" },
              { "order": 4, "name": "animeCategories", "label": "Anime Categories", "value": [5070], "type": "select" },
              { "order": 5, "name": "animeStandardFormatSearch", "label": "Anime Standard Format Search", "value": false, "type": "checkbox" },
              { "order": 6, "name": "additionalParameters", "label": "Additional Parameters", "type": "textbox", "advanced": true },
              { "order": 7, "name": "multiLanguages", "value": [] },
              { "order": 8, "name": "failDownloads", "value": [] }
            ],
            "implementationName": "Newznab", "implementation": "Newznab", "configContract": "NewznabSettings", "tags": [], "id": 4
          }
        ]
        """;

    public static string RadarrClients => """
        [
          {
            "enable": true, "protocol": "usenet", "priority": 1, "removeCompletedDownloads": true, "removeFailedDownloads": true, "name": "SABnzbd",
            "fields": [
              { "order": 0, "name": "host", "value": "192.168.1.10" }, { "order": 1, "name": "port", "value": 8080 },
              { "order": 2, "name": "useSsl", "value": false }, { "order": 3, "name": "urlBase", "value": "/sabnzbd" },
              { "order": 4, "name": "apiKey", "value": "********" }, { "order": 5, "name": "username" }, { "order": 6, "name": "password", "value": "********" },
              { "order": 7, "name": "movieCategory", "value": "movies" }, { "order": 8, "name": "recentMoviePriority", "value": -100 }
            ],
            "implementationName": "SABnzbd", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "tags": [], "id": 1
          },
          {
            "enable": false, "protocol": "torrent", "priority": 1, "name": "qBittorrent",
            "fields": [ { "name": "host", "value": "localhost" }, { "name": "port", "value": 8090 } ],
            "implementation": "QBittorrent", "tags": [], "id": 2
          }
        ]
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != ApiKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var body = request.RequestUri!.AbsolutePath switch
        {
            "/api/v3/system/status" => $$"""{"appName": "{{App}}", "instanceName": "{{App}}", "version": "{{(App == "Sonarr" ? "4.0.15.2941" : "5.26.2.10099")}}"}""",
            "/api/v3/indexer" => IndexerJson ?? (App == "Sonarr" ? SonarrIndexers : RadarrIndexers),
            "/api/v3/remotepathmapping" => RemotePathMappingJson,
            "/api/v3/downloadclient" => DownloadClientJson ?? (App == "Sonarr" ? RadarrClients.Replace("movieCategory", "tvCategory", StringComparison.Ordinal).Replace("\"movies\"", "\"tv\"", StringComparison.Ordinal) : RadarrClients),
            _ => null,
        };
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
