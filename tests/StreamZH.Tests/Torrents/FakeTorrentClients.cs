// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StreamZH.Core.Downloads;
using StreamZH.Library.Downloads.Torrent;

namespace StreamZH.Tests.Torrents;

/// <summary>Tiny .torrent files of generated data (never real content) and magnet links for them.</summary>
public static class TorrentFixtures
{
    public static byte[] Create(string name, long size = 1_048_576, string tracker = "http://tracker.example.org/announce")
    {
        var pieces = SHA1.HashData(Encoding.UTF8.GetBytes(name));
        using var stream = new MemoryStream();
        void Write(string text) => stream.Write(Encoding.UTF8.GetBytes(text));
        void Str(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Write(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
            stream.Write(bytes);
        }

        Write("d");
        Str("announce");
        Str(tracker);
        Str("info");
        Write("d");
        Str("length");
        Write($"i{size.ToString(CultureInfo.InvariantCulture)}e");
        Str("name");
        Str(name);
        Str("piece length");
        Write("i262144e");
        Str("pieces");
        Write("20:");
        stream.Write(pieces);
        Write("e");
        Write("e");
        return stream.ToArray();
    }

    public static string Magnet(string name) =>
        $"magnet:?xt=urn:btih:{TorrentMetadata.InfoHash(Create(name))}&dn={Uri.EscapeDataString(name)}&tr={Uri.EscapeDataString("http://tracker.example.org/announce")}";
}

/// <summary>Where a fake torrent is in its life; each fake client shows it in its own words.</summary>
public enum FakePhase
{
    Queued,
    Downloading,
    Stalled,
    Paused,
    Seeding,

    /// <summary>Done and stopped (seed limit reached).</summary>
    Stopped,
    Error,
}

public sealed class FakeTorrent
{
    public required string Hash { get; init; }

    public string Name { get; set; } = "Fake.Torrent";

    public long Size { get; set; } = 1_048_576;

    public double Progress { get; set; }

    public FakePhase Phase { get; set; } = FakePhase.Downloading;

    public string? Category { get; set; }

    public string SavePath { get; set; } = "/downloads";

    public double Ratio { get; set; }

    public long Uploaded { get; set; }

    public long SeedingSeconds { get; set; }

    public double? RatioLimit { get; set; }

    public int? SeedTimeLimit { get; set; }

    public bool AddedPaused { get; set; }

    public bool FromMagnet { get; set; }

    public bool Done => Phase is FakePhase.Seeding or FakePhase.Stopped;

    public long Downloaded => Done ? Size : (long)(Size * Progress);
}

/// <summary>An in-process HTTP server (Kestrel on 127.0.0.1, random port) with the torrents of a fake client.</summary>
public abstract class FakeTorrentServer : IAsyncDisposable
{
    private WebApplication? _app;

    public ConcurrentDictionary<string, FakeTorrent> Torrents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; private set; }

    /// <summary>Requests per path (or RPC method), for assertions.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public List<(string Hash, bool DeleteData)> Removed { get; } = [];

    /// <summary>When set, every request fails with 500 (the client is "down").</summary>
    public bool Broken { get; set; }

    public FakeTorrent Torrent(string hash) => Torrents[hash];

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (Broken)
            {
                context.Response.StatusCode = 500;
                return;
            }

            await next();
        });
        Map(_app);
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    public Dictionary<string, JsonElement> Settings(string? username = null, string? password = null, string urlBase = "", string? category = DownloadClientCatalog.DefaultCategory)
    {
        var settings = new Dictionary<string, JsonElement>
        {
            [DownloadClientCatalog.Host] = JsonSerializer.SerializeToElement("127.0.0.1"),
            [DownloadClientCatalog.Port] = JsonSerializer.SerializeToElement(Port),
            [DownloadClientCatalog.UseSsl] = JsonSerializer.SerializeToElement(false),
            [DownloadClientCatalog.UrlBase] = JsonSerializer.SerializeToElement(urlBase),
            [DownloadClientCatalog.Category] = JsonSerializer.SerializeToElement(category ?? string.Empty),
        };
        if (username is not null)
        {
            settings[DownloadClientCatalog.Username] = JsonSerializer.SerializeToElement(username);
        }

        if (password is not null)
        {
            settings[DownloadClientCatalog.Password] = JsonSerializer.SerializeToElement(password);
        }

        return settings;
    }

    protected abstract void Map(WebApplication app);

    protected FakeTorrent AddTorrent(byte[]? file, string? magnet, string? category, string? savePath)
    {
        var hash = file is not null ? TorrentMetadata.InfoHash(file) : TorrentMetadata.ParseMagnet(magnet)!.InfoHash;
        var torrent = new FakeTorrent
        {
            Hash = hash,
            Name = file is not null ? TorrentMetadata.Name(file) ?? hash : TorrentMetadata.ParseMagnet(magnet)!.Name ?? hash,
            Category = category,
            SavePath = savePath ?? "/downloads",
            FromMagnet = file is null,
            Phase = FakePhase.Downloading,
        };
        Torrents[hash] = torrent;
        return torrent;
    }
}

/// <summary>qBittorrent WebUI API v2 (4.x and 5.x shapes): cookie login, categories, add (multipart), info, delete, share limits.</summary>
public sealed class FakeQBittorrent(string version = "v5.0.4", string apiVersion = "2.11.2") : FakeTorrentServer
{
    private string? _session;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public int Logins { get; private set; }

    public HashSet<string> Categories { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> LastAdd { get; } = [];

    /// <summary>Forget the session (like a qBittorrent restart): the next request gets 403.</summary>
    public void ExpireSession() => _session = null;

    private bool V5 => Version.Parse(apiVersion) >= new Version(2, 11, 0);

    protected override void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            Calls.Enqueue(context.Request.Path.Value!);
            var path = context.Request.Path.Value!;
            if (Username is not null && path != "/api/v2/auth/login"
                && (_session is null || context.Request.Cookies["SID"] != _session))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden");
                return;
            }

            await next();
        });

        app.MapPost("/api/v2/auth/login", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            if (form["username"] != Username || form["password"] != Password)
            {
                return Results.Text("Fails.");
            }

            Logins++;
            _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            context.Response.Headers.Append("Set-Cookie", $"SID={_session}; HttpOnly; SameSite=Strict; path=/");
            return Results.Text("Ok.");
        });
        app.MapGet("/api/v2/app/version", () => Results.Text(version));
        app.MapGet("/api/v2/app/webapiVersion", () => Results.Text(apiVersion));
        app.MapGet("/api/v2/torrents/categories", () =>
            Results.Json(Categories.ToDictionary(c => c, c => new { name = c, savePath = string.Empty })));
        app.MapPost("/api/v2/torrents/createCategory", async (HttpContext context) =>
        {
            var name = (await context.Request.ReadFormAsync())["category"].ToString();
            if (name.Length == 0)
            {
                return Results.StatusCode(400);
            }

            return Categories.Add(name) ? Results.Ok() : Results.StatusCode(409);
        });
        app.MapPost("/api/v2/torrents/add", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            LastAdd.Clear();
            foreach (var (key, value) in form)
            {
                LastAdd[key] = value.ToString();
            }

            byte[]? file = null;
            if (form.Files.GetFile("torrents") is { } upload)
            {
                using var buffer = new MemoryStream();
                await upload.CopyToAsync(buffer);
                file = buffer.ToArray();
                if (!TorrentMetadata.LooksLikeTorrent(file))
                {
                    return V5 ? Results.Text("Fails.", statusCode: 415) : Results.Text("Fails.");
                }
            }

            var magnet = form["urls"].ToString();
            if (file is null && TorrentMetadata.ParseMagnet(magnet) is null)
            {
                return Results.Text("Fails.");
            }

            var category = form["category"].ToString();
            var torrent = AddTorrent(file, file is null ? magnet : null, category.Length == 0 ? null : category, form.TryGetValue("savepath", out var save) ? save.ToString() : null);
            torrent.AddedPaused = form[V5 ? "stopped" : "paused"] == "true";
            torrent.Phase = torrent.AddedPaused ? FakePhase.Paused : FakePhase.Downloading;
            if (double.TryParse(form["ratioLimit"], CultureInfo.InvariantCulture, out var ratio) && ratio >= 0)
            {
                torrent.RatioLimit = ratio;
            }

            if (int.TryParse(form["seedingTimeLimit"], CultureInfo.InvariantCulture, out var minutes) && minutes >= 0)
            {
                torrent.SeedTimeLimit = minutes;
            }

            return Results.Text("Ok.");
        });
        app.MapGet("/api/v2/torrents/info", (string? category, string? hashes) =>
        {
            var list = Torrents.Values
                .Where(t => category is null || t.Category == category)
                .Where(t => hashes is null || hashes.Split('|').Contains(t.Hash, StringComparer.OrdinalIgnoreCase))
                .Select(ToJson)
                .ToList();
            return Results.Json(list);
        });
        app.MapPost("/api/v2/torrents/delete", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var hash in form["hashes"].ToString().Split('|'))
            {
                if (Torrents.TryRemove(hash, out _))
                {
                    Removed.Add((hash, form["deleteFiles"] == "true"));
                }
            }

            return Results.Ok();
        });
        app.MapPost("/api/v2/torrents/setShareLimits", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var hash in form["hashes"].ToString().Split('|'))
            {
                if (Torrents.TryGetValue(hash, out var t))
                {
                    t.RatioLimit = double.Parse(form["ratioLimit"]!, CultureInfo.InvariantCulture) is >= 0 and var r ? r : null;
                    t.SeedTimeLimit = int.Parse(form["seedingTimeLimit"]!, CultureInfo.InvariantCulture) is >= 0 and var m ? m : null;
                }
            }

            return Results.Ok();
        });
    }

    private string State(FakeTorrent t) => t.Phase switch
    {
        FakePhase.Queued => "queuedDL",
        FakePhase.Downloading => "downloading",
        FakePhase.Stalled => "stalledDL",
        FakePhase.Paused => V5 ? "stoppedDL" : "pausedDL",
        FakePhase.Seeding => "uploading",
        FakePhase.Stopped => V5 ? "stoppedUP" : "pausedUP",
        _ => "error",
    };

    // The fields of /api/v2/torrents/info (a subset of the real list, same names and types).
    private object ToJson(FakeTorrent t) => new Dictionary<string, object?>
    {
        ["added_on"] = 1760000000,
        ["amount_left"] = t.Size - t.Downloaded,
        ["category"] = t.Category ?? string.Empty,
        ["completion_on"] = t.Done ? 1760003600 : -1,
        ["content_path"] = $"{t.SavePath.TrimEnd('/')}/{t.Name}",
        ["dlspeed"] = t.Phase == FakePhase.Downloading ? 524288 : 0,
        ["downloaded"] = t.Downloaded,
        ["eta"] = t.Done ? 8640000 : 120,
        ["hash"] = t.Hash,
        ["name"] = t.Name,
        ["num_leechs"] = 2,
        ["num_seeds"] = 7,
        ["progress"] = t.Done ? 1.0 : t.Progress,
        ["ratio"] = t.Ratio,
        ["ratio_limit"] = t.RatioLimit ?? -2,
        ["save_path"] = t.SavePath,
        ["seeding_time"] = t.SeedingSeconds,
        ["seeding_time_limit"] = t.SeedTimeLimit ?? -2,
        ["size"] = t.Size,
        ["state"] = State(t),
        ["tags"] = string.Empty,
        ["total_size"] = t.Size,
        ["uploaded"] = t.Uploaded,
        ["upspeed"] = t.Phase == FakePhase.Seeding ? 65536 : 0,
    };
}

/// <summary>Transmission RPC (rpc-spec.md): X-Transmission-Session-Id handshake, basic auth, labels from RPC 16.</summary>
public sealed class FakeTransmission(int rpcVersion = 17, string version = "4.0.6 (38c164933e)") : FakeTorrentServer
{
    public string SessionId { get; set; } = "session-1";

    public string? Username { get; set; }

    public string? Password { get; set; }

    public int Handshakes { get; private set; }

    public JsonObject? LastAdd { get; private set; }

    protected override void Map(WebApplication app)
    {
        app.MapPost("/transmission/rpc", async (HttpContext context) =>
        {
            if (Username is not null)
            {
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
                if (context.Request.Headers.Authorization != expected)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }

            if (context.Request.Headers["X-Transmission-Session-Id"] != SessionId)
            {
                Handshakes++;
                context.Response.StatusCode = 409;
                context.Response.Headers["X-Transmission-Session-Id"] = SessionId;
                await context.Response.WriteAsync("<h1>409: Conflict</h1>");
                return;
            }

            var body = JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())!.AsObject();
            var method = body["method"]!.GetValue<string>();
            Calls.Enqueue(method);
            var arguments = body["arguments"] as JsonObject ?? [];
            var (result, answer) = Handle(method, arguments);
            await context.Response.WriteAsJsonAsync(new JsonObject { ["arguments"] = answer, ["result"] = result });
        });
    }

    private (string Result, JsonObject Arguments) Handle(string method, JsonObject arguments)
    {
        switch (method)
        {
            case "session-get":
                return ("success", new JsonObject
                {
                    ["version"] = version,
                    ["rpc-version"] = rpcVersion,
                    ["download-dir"] = "/downloads",
                    ["seedRatioLimit"] = 2.0,
                    ["seedRatioLimited"] = false,
                });
            case "torrent-add":
                LastAdd = (JsonObject)arguments.DeepClone();
                var file = arguments["metainfo"]?.GetValue<string>() is { } meta ? Convert.FromBase64String(meta) : null;
                var magnet = arguments["filename"]?.GetValue<string>();
                if (file is null && TorrentMetadata.ParseMagnet(magnet) is null)
                {
                    return ("invalid or corrupt torrent file", []);
                }

                var hash = file is not null ? TorrentMetadata.InfoHash(file) : TorrentMetadata.ParseMagnet(magnet)!.InfoHash;
                if (Torrents.TryGetValue(hash, out var duplicate))
                {
                    return ("success", new JsonObject { ["torrent-duplicate"] = new JsonObject { ["hashString"] = duplicate.Hash, ["id"] = 1, ["name"] = duplicate.Name } });
                }

                var labels = arguments["labels"] is JsonArray l && l.Count > 0 ? l[0]!.GetValue<string>() : null;
                var torrent = AddTorrent(file, file is null ? magnet : null, rpcVersion >= 16 ? labels : null, arguments["download-dir"]?.GetValue<string>());
                torrent.AddedPaused = arguments["paused"]?.GetValue<bool>() == true;
                torrent.Phase = torrent.AddedPaused ? FakePhase.Paused : FakePhase.Downloading;
                return ("success", new JsonObject { ["torrent-added"] = new JsonObject { ["hashString"] = hash, ["id"] = Torrents.Count, ["name"] = torrent.Name } });
            case "torrent-set":
                foreach (var id in (arguments["ids"] as JsonArray ?? []).Select(i => i!.GetValue<string>()))
                {
                    if (Torrents.TryGetValue(id, out var t))
                    {
                        t.RatioLimit = arguments["seedRatioMode"]?.GetValue<int>() == 1 ? arguments["seedRatioLimit"]!.GetValue<double>() : t.RatioLimit;
                        t.SeedTimeLimit = arguments["seedIdleMode"]?.GetValue<int>() == 1 ? arguments["seedIdleLimit"]!.GetValue<int>() : t.SeedTimeLimit;
                    }
                }

                return ("success", []);
            case "torrent-get":
                var list = new JsonArray();
                var index = 0;
                foreach (var t in Torrents.Values)
                {
                    list.Add(ToJson(t, ++index));
                }

                return ("success", new JsonObject { ["torrents"] = list });
            case "torrent-remove":
                foreach (var id in (arguments["ids"] as JsonArray ?? []).Select(i => i!.GetValue<string>()))
                {
                    if (Torrents.TryRemove(id, out _))
                    {
                        Removed.Add((id, arguments["delete-local-data"]?.GetValue<bool>() == true));
                    }
                }

                return ("success", []);
            default:
                return ("method name not recognized", []);
        }
    }

    private JsonObject ToJson(FakeTorrent t, int id) => new()
    {
        ["id"] = id,
        ["hashString"] = t.Hash,
        ["name"] = t.Name,
        ["downloadDir"] = t.SavePath,
        ["totalSize"] = t.Size,
        ["leftUntilDone"] = t.Size - t.Downloaded,
        ["isFinished"] = t.Phase == FakePhase.Stopped,
        ["eta"] = t.Done ? -1 : 120,
        ["status"] = t.Phase switch
        {
            FakePhase.Queued => 3,
            FakePhase.Downloading or FakePhase.Stalled => 4,
            FakePhase.Seeding => 6,
            _ => 0,
        },
        ["secondsSeeding"] = t.SeedingSeconds,
        ["error"] = t.Phase == FakePhase.Error ? 3 : 0,
        ["errorString"] = t.Phase == FakePhase.Error ? "No data found! Ensure your drives are connected" : string.Empty,
        ["uploadedEver"] = t.Uploaded,
        ["downloadedEver"] = t.Downloaded,
        ["uploadRatio"] = t.Ratio,
        ["rateDownload"] = t.Phase == FakePhase.Downloading ? 524288 : 0,
        ["rateUpload"] = 0,
        ["peersSendingToUs"] = 5,
        ["peersGettingFromUs"] = 1,
        ["addedDate"] = 1760000000,
        ["doneDate"] = t.Done ? 1760003600 : 0,
        ["labels"] = t.Category is null ? new JsonArray() : new JsonArray(t.Category),
    };
}

/// <summary>Deluge Web UI JSON-RPC: auth.login cookie, daemon connection, Label plugin, web.update_ui.</summary>
public sealed class FakeDeluge(string version = "2.1.1") : FakeTorrentServer
{
    private string? _session;

    public string Password { get; set; } = "deluge";

    public bool DaemonConnected { get; set; }

    public bool LabelPlugin { get; set; } = true;

    public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);

    public int Logins { get; private set; }

    public JsonNode? LastAddOptions { get; private set; }

    public void ExpireSession() => _session = null;

    protected override void Map(WebApplication app)
    {
        app.MapPost("/json", async (HttpContext context) =>
        {
            var body = JsonNode.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync())!.AsObject();
            var method = body["method"]!.GetValue<string>();
            var parameters = body["params"] as JsonArray ?? [];
            var id = body["id"]?.GetValue<int>() ?? 0;
            Calls.Enqueue(method);
            if (method == "auth.login")
            {
                var ok = parameters.Count > 0 && parameters[0]?.GetValue<string>() == Password;
                if (ok)
                {
                    Logins++;
                    _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                    context.Response.Headers.Append("Set-Cookie", $"_session_id={_session}; Expires=Tue, 01 Jan 2030 00:00:00 GMT; Path=/json");
                }

                await context.Response.WriteAsJsonAsync(new JsonObject { ["result"] = ok, ["error"] = null, ["id"] = id });
                return;
            }

            if (_session is null || context.Request.Cookies["_session_id"] != _session)
            {
                await context.Response.WriteAsJsonAsync(new JsonObject
                {
                    ["result"] = null,
                    ["error"] = new JsonObject { ["message"] = "Not authenticated", ["code"] = 1 },
                    ["id"] = id,
                });
                return;
            }

            JsonNode? result;
            try
            {
                result = Handle(method, parameters);
            }
            catch (InvalidOperationException ex)
            {
                await context.Response.WriteAsJsonAsync(new JsonObject
                {
                    ["result"] = null,
                    ["error"] = new JsonObject { ["message"] = ex.Message, ["code"] = 2 },
                    ["id"] = id,
                });
                return;
            }

            await context.Response.WriteAsJsonAsync(new JsonObject { ["result"] = result, ["error"] = null, ["id"] = id });
        });
    }

    private JsonNode? Handle(string method, JsonArray parameters)
    {
        switch (method)
        {
            case "web.connected":
                return DaemonConnected;
            case "web.get_hosts":
                return new JsonArray(new JsonArray("c3f2d4b1e5a6", "127.0.0.1", 58846, "localclient"));
            case "web.connect":
                DaemonConnected = parameters[0]!.GetValue<string>() == "c3f2d4b1e5a6";
                return new JsonArray();
            case "daemon.get_version":
                return version;
            case "core.get_enabled_plugins":
                return LabelPlugin ? new JsonArray("Label") : new JsonArray();
            case "label.get_labels":
                return LabelPlugin ? new JsonArray([.. Labels.Select(l => (JsonNode)l)]) : throw new InvalidOperationException("Unknown method");
            case "label.add":
                if (!Labels.Add(parameters[0]!.GetValue<string>()))
                {
                    throw new InvalidOperationException("Label already exists");
                }

                return null;
            case "label.set_torrent":
                Torrents[parameters[0]!.GetValue<string>()].Category = parameters[1]!.GetValue<string>();
                return null;
            case "core.add_torrent_file":
            {
                LastAddOptions = parameters[2]?.DeepClone();
                var file = Convert.FromBase64String(parameters[1]!.GetValue<string>());
                var hash = TorrentMetadata.InfoHash(file);
                if (Torrents.ContainsKey(hash))
                {
                    return null;
                }

                var torrent = AddTorrent(file, null, null, parameters[2]?["download_location"]?.GetValue<string>());
                torrent.AddedPaused = parameters[2]?["add_paused"]?.GetValue<bool>() == true;
                torrent.Phase = torrent.AddedPaused ? FakePhase.Paused : FakePhase.Downloading;
                return hash;
            }

            case "core.add_torrent_magnet":
            {
                LastAddOptions = parameters[1]?.DeepClone();
                var torrent = AddTorrent(null, parameters[0]!.GetValue<string>(), null, parameters[1]?["download_location"]?.GetValue<string>());
                return torrent.Hash;
            }

            case "core.set_torrent_options":
                foreach (var hash in parameters[0]!.AsArray().Select(h => h!.GetValue<string>()))
                {
                    if (Torrents.TryGetValue(hash, out var t) && parameters[1]?["stop_ratio"] is { } ratio)
                    {
                        t.RatioLimit = ratio.GetValue<double>();
                    }
                }

                return null;
            case "web.update_ui":
                if (!DaemonConnected)
                {
                    return new JsonObject { ["torrents"] = new JsonObject(), ["connected"] = false };
                }

                var label = parameters.Count > 1 ? parameters[1]?["label"]?.GetValue<string>() : null;
                var torrents = new JsonObject();
                foreach (var t in Torrents.Values.Where(t => label is null || t.Category == label))
                {
                    torrents[t.Hash] = ToJson(t);
                }

                return new JsonObject { ["torrents"] = torrents, ["filters"] = new JsonObject(), ["connected"] = true, ["stats"] = new JsonObject() };
            case "core.remove_torrent":
                if (!Torrents.TryRemove(parameters[0]!.GetValue<string>(), out _))
                {
                    throw new InvalidOperationException("Torrent ID not found in session.");
                }

                Removed.Add((parameters[0]!.GetValue<string>(), parameters[1]!.GetValue<bool>()));
                return true;
            default:
                throw new InvalidOperationException($"Unknown method {method}");
        }
    }

    private static JsonObject ToJson(FakeTorrent t) => new()
    {
        ["hash"] = t.Hash,
        ["name"] = t.Name,
        ["state"] = t.Phase switch
        {
            FakePhase.Queued => "Queued",
            FakePhase.Downloading or FakePhase.Stalled => "Downloading",
            FakePhase.Paused or FakePhase.Stopped => "Paused",
            FakePhase.Seeding => "Seeding",
            _ => "Error",
        },
        ["progress"] = t.Done ? 100.0 : t.Progress * 100,
        ["eta"] = t.Done ? 0 : 120,
        ["message"] = t.Phase == FakePhase.Error ? "Error: No such file or directory" : "OK",
        ["is_finished"] = t.Done,
        ["save_path"] = t.SavePath,
        ["total_size"] = t.Size,
        ["total_done"] = t.Downloaded,
        ["total_uploaded"] = t.Uploaded,
        ["time_added"] = 1760000000.5,
        ["completed_time"] = t.Done ? 1760003600 : 0,
        ["seeding_time"] = t.SeedingSeconds,
        ["ratio"] = t.Done ? t.Ratio : -1,
        ["num_seeds"] = 4,
        ["num_peers"] = 3,
        ["download_payload_rate"] = t.Phase == FakePhase.Downloading ? 524288 : 0,
        ["upload_payload_rate"] = 0,
        ["label"] = t.Category ?? string.Empty,
    };
}

/// <summary>An IHttpClientFactory for tests: the torrent clients' named client without proxy.</summary>
public sealed class DirectHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
