// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text.Json;
using MimeKit;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Connect;

public sealed class NotificationSenderTests
{
    private readonly FakeHttp _http = new();

    [Fact]
    public async Task Discord_posts_an_embed_with_the_chosen_fields()
    {
        var context = ConnectTestData.Context("discord", new
        {
            webHookUrl = "https://discord.com/api/webhooks/1/abc",
            username = "Media Bot",
            avatar = "https://example.com/a.png",
            importFields = new[] { "quality", "group", "size", "links", "poster", "fanart", "overview" },
        });

        await new DiscordSender(_http).SendAsync(context, ConnectTestData.MovieImport(), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://discord.com/api/webhooks/1/abc", request.Uri.ToString());
        var json = request.Json;
        Assert.Equal("Media Bot", json.GetProperty("username").GetString());
        Assert.Equal("https://example.com/a.png", json.GetProperty("avatar_url").GetString());
        var embed = Assert.Single(json.GetProperty("embeds").EnumerateArray());
        Assert.Equal("Movie Downloaded", embed.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("Dune (2021)", embed.GetProperty("title").GetString());
        Assert.Equal("https://www.themoviedb.org/movie/438631", embed.GetProperty("url").GetString());
        Assert.Equal("Paul Atreides...", embed.GetProperty("description").GetString());
        Assert.Equal(0x27C24C, embed.GetProperty("color").GetInt32());
        var fields = embed.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("WEBDL-1080p", fields["Quality"]);
        Assert.Equal("FLUX", fields["Group"]);
        Assert.Equal("4.7 GB", fields["Size"]);
        Assert.Equal("[TMDB](https://www.themoviedb.org/movie/438631) / [IMDb](https://www.imdb.com/title/tt1160419/)", fields["Links"]);
        Assert.False(fields.ContainsKey("Indexer"));
        Assert.Equal("https://image.tmdb.org/t/p/w500/dune.jpg", embed.GetProperty("thumbnail").GetProperty("url").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w1280/dune-back.jpg", embed.GetProperty("image").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Discord_grab_uses_grab_fields_and_rate_limits_are_retryable()
    {
        var context = ConnectTestData.Context("discord", new { webHookUrl = "https://discord.com/api/webhooks/1/abc" });

        await new DiscordSender(_http).SendAsync(context, ConnectTestData.EpisodeGrab(), default);
        var embed = _http.Last.Json.GetProperty("embeds")[0];
        Assert.Equal("Episode Grabbed", embed.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("Breaking Bad (2008) · S01E02 Cat's in the Bag...", embed.GetProperty("title").GetString());
        Assert.Contains(embed.GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "Indexer" && f.GetProperty("value").GetString() == "NZBgeek");
        Assert.Equal("StreamZH", _http.Last.Json.GetProperty("username").GetString());

        _http.Respond = _ => (HttpStatusCode.TooManyRequests, """{"message":"You are being rate limited."}""");
        var ex = await Assert.ThrowsAsync<ConnectException>(() => new DiscordSender(_http).SendAsync(context, ConnectTestData.Health(), default));
        Assert.True(ex.Retryable);
        Assert.Equal("rate limited: You are being rate limited.", ex.Message);
    }

    [Fact]
    public async Task Telegram_sends_html_silently_into_a_topic()
    {
        var context = ConnectTestData.Context("telegram", new { botToken = "123:ABC", chatId = "-100777", topicId = 42, sendSilently = true });

        await new TelegramSender(_http).SendAsync(context, ConnectTestData.EpisodeGrab(), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", request.Uri.ToString());
        var json = request.Json;
        Assert.Equal("-100777", json.GetProperty("chat_id").GetString());
        Assert.Equal(42, json.GetProperty("message_thread_id").GetInt32());
        Assert.True(json.GetProperty("disable_notification").GetBoolean());
        Assert.Equal("HTML", json.GetProperty("parse_mode").GetString());
        var text = json.GetProperty("text").GetString()!;
        Assert.StartsWith("<b>Episode Grabbed</b>\nBreaking Bad (2008) · S01E02 Cat's in the Bag... · Bluray-1080p · ROVERS", text, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://www.themoviedb.org/tv/1396\">TMDB</a>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_error_description_is_shown()
    {
        _http.Respond = _ => (HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");
        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            new TelegramSender(_http).TestAsync(ConnectTestData.Context("telegram", new { botToken = "1:A", chatId = "5" }), default));

        Assert.Equal("rejected (400): Bad Request: chat not found", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Pushover_sends_priority_devices_sound_and_emergency_settings()
    {
        var context = ConnectTestData.Context("pushover", new { apiKey = "app", userKey = "user", devices = "phone, tablet", priority = "2", retry = 120, expire = 600, sound = "magic" });

        await new PushoverSender(_http).SendAsync(context, ConnectTestData.MovieImport(upgrade: true), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://api.pushover.net/1/messages.json", request.Uri.ToString());
        var form = request.Body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("app", form["token"]);
        Assert.Equal("user", form["user"]);
        Assert.Equal("StreamZH - Movie Upgraded", form["title"]);
        Assert.StartsWith("Dune (2021) · WEBDL-1080p · FLUX\nSize: 4.7 GB", form["message"], StringComparison.Ordinal);
        Assert.Equal("2", form["priority"]);
        Assert.Equal("120", form["retry"]);
        Assert.Equal("600", form["expire"]);
        Assert.Equal("phone,tablet", form["device"]);
        Assert.Equal("magic", form["sound"]);

        _http.Respond = _ => (HttpStatusCode.BadRequest, """{"user":"invalid","errors":["user identifier is invalid"],"status":0}""");
        var ex = await Assert.ThrowsAsync<ConnectException>(() => new PushoverSender(_http).TestAsync(context, default));
        Assert.Equal("rejected (400): user identifier is invalid", ex.Message);
    }

    [Fact]
    public async Task Ntfy_publishes_to_every_topic_with_token_priority_tags_and_poster()
    {
        var context = ConnectTestData.Context("ntfy", new { serverUrl = "https://ntfy.example.com/", topics = "movies, alerts", accessToken = "tk_123", priority = "4", tags = "movie_camera,tada" });

        await new NtfySender(_http).SendAsync(context, ConnectTestData.MovieImport(), default);

        Assert.Equal(2, _http.Requests.Count);
        Assert.All(_http.Requests, r => Assert.Equal("https://ntfy.example.com/", r.Uri.ToString()));
        Assert.All(_http.Requests, r => Assert.Equal("Bearer tk_123", r.Header("Authorization")));
        Assert.Equal(["movies", "alerts"], _http.Requests.Select(r => r.Json.GetProperty("topic").GetString()));
        var json = _http.Requests[0].Json;
        Assert.Equal("StreamZH - Movie Downloaded", json.GetProperty("title").GetString());
        Assert.Equal(4, json.GetProperty("priority").GetInt32());
        Assert.Equal(["movie_camera", "tada"], json.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("https://www.themoviedb.org/movie/438631", json.GetProperty("click").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/dune.jpg", json.GetProperty("attach").GetString());
    }

    [Fact]
    public async Task Ntfy_uses_basic_auth_and_the_default_server()
    {
        var type = ConnectCatalog.Find("ntfy")!;
        Assert.Equal("https://ntfy.sh", type.Fields.Single(f => f.Name == "serverUrl").Default);
        var context = ConnectTestData.Context("ntfy", new { topics = "streamzh-x", username = "me", password = "pw", includePoster = false });

        await new NtfySender(_http).SendAsync(context, ConnectTestData.Health("error"), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://ntfy.sh/", request.Uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String("me:pw"u8.ToArray()), request.Header("Authorization"));
        Assert.Equal("StreamZH - Health Error", request.Json.GetProperty("title").GetString());
        Assert.False(request.Json.TryGetProperty("attach", out _));
    }

    [Fact]
    public async Task Gotify_sends_markdown_with_the_poster_and_the_app_token()
    {
        var context = ConnectTestData.Context("gotify", new { server = "https://gotify.example.com", appToken = "AbC", priority = "8" });

        await new GotifySender(_http).SendAsync(context, ConnectTestData.MovieImport(), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://gotify.example.com/message", request.Uri.ToString());
        Assert.Equal("AbC", request.Header("X-Gotify-Key"));
        var json = request.Json;
        Assert.Equal(8, json.GetProperty("priority").GetInt32());
        Assert.Contains("![](https://image.tmdb.org/t/p/w500/dune.jpg)", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("text/markdown", json.GetProperty("extras").GetProperty("client::display").GetProperty("contentType").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/dune.jpg", json.GetProperty("extras").GetProperty("client::notification").GetProperty("bigImageUrl").GetString());
    }

    [Fact]
    public async Task Apprise_uses_the_configuration_key_or_stateless_urls()
    {
        var sender = new AppriseSender(_http);
        await sender.SendAsync(ConnectTestData.Context("apprise", new { serverUrl = "http://apprise:8000", configurationKey = "streamzh", tags = "tv,admins", username = "a", password = "b" }), ConnectTestData.MovieImport(), default);
        var keyed = _http.Last;
        Assert.Equal("http://apprise:8000/notify/streamzh", keyed.Uri.ToString());
        Assert.Equal("tv,admins", keyed.Json.GetProperty("tag").GetString());
        Assert.Equal("info", keyed.Json.GetProperty("type").GetString());
        Assert.False(keyed.Json.TryGetProperty("urls", out _));
        Assert.StartsWith("Basic ", keyed.Header("Authorization"), StringComparison.Ordinal);

        await sender.SendAsync(ConnectTestData.Context("apprise", new { serverUrl = "http://apprise:8000/", statelessUrls = "tgram://bot/chat" }), ConnectTestData.Health(), default);
        Assert.Equal("http://apprise:8000/notify", _http.Last.Uri.ToString());
        Assert.Equal("tgram://bot/chat", _http.Last.Json.GetProperty("urls").GetString());
        Assert.Equal("warning", _http.Last.Json.GetProperty("type").GetString());

        var errors = new Dictionary<string, string[]>();
        sender.Validate(new ConnectionSettings(new Dictionary<string, JsonElement>()), errors);
        Assert.True(errors.ContainsKey("configurationKey"));
    }

    [Fact]
    public async Task Webhook_sends_the_radarr_style_payload_with_auth_and_headers()
    {
        var context = ConnectTestData.Context("webhook", new { url = "https://hooks.example.com/in", method = "PUT", username = "u", password = "p", headers = "X-Token: abc\nbroken line\nX-Other: 1" });

        await new WebhookSender(_http).SendAsync(context, ConnectTestData.MovieImport(upgrade: true), default);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("abc", request.Header("X-Token"));
        Assert.Equal("1", request.Header("X-Other"));
        Assert.Equal("Basic " + Convert.ToBase64String("u:p"u8.ToArray()), request.Header("Authorization"));
        var json = request.Json;
        Assert.Equal("Download", json.GetProperty("eventType").GetString());
        Assert.Equal("My StreamZH", json.GetProperty("instanceName").GetString());
        Assert.True(json.GetProperty("isUpgrade").GetBoolean());
        Assert.Equal("12", json.GetProperty("downloadId").GetString());
        var movie = json.GetProperty("movie");
        Assert.Equal(("Dune", 2021, 438631, "tt1160419"), (movie.GetProperty("title").GetString(), movie.GetProperty("year").GetInt32(), movie.GetProperty("tmdbId").GetInt32(), movie.GetProperty("imdbId").GetString()));
        Assert.Equal("/media/movies/Dune (2021)", movie.GetProperty("folderPath").GetString());
        Assert.Equal("poster", movie.GetProperty("images")[0].GetProperty("coverType").GetString());
        Assert.Equal("WEBDL-1080p", json.GetProperty("release").GetProperty("quality").GetString());
        Assert.Equal("FLUX", json.GetProperty("release").GetProperty("releaseGroup").GetString());
        Assert.Equal("Dune (2021).mkv", json.GetProperty("movieFile").GetProperty("relativePath").GetString());
        Assert.Equal(5_000_000_000, json.GetProperty("movieFile").GetProperty("size").GetInt64());
    }

    [Fact]
    public void Webhook_payload_for_series_health_and_failures()
    {
        var grab = WebhookSender.Payload(ConnectTestData.EpisodeGrab(), "x");
        Assert.Equal("Grab", grab["eventType"]!.GetValue<string>());
        Assert.Equal(81189, grab["series"]!["tvdbId"]!.GetValue<int>());
        Assert.Equal("/media/tv/Breaking Bad", grab["series"]!["path"]!.GetValue<string>());
        Assert.Equal(2, grab["episodes"]![0]!["episodeNumber"]!.GetValue<int>());
        Assert.Equal("NZBgeek", grab["release"]!["indexer"]!.GetValue<string>());
        Assert.Null(grab["isUpgrade"]);

        var health = WebhookSender.Payload(ConnectTestData.Health("error"), "x");
        Assert.Equal("Health", health["eventType"]!.GetValue<string>());
        Assert.Equal("error", health["level"]!.GetValue<string>());
        Assert.Equal("no-indexer", health["type"]!.GetValue<string>());
        Assert.StartsWith("No indexer configured: ", health["message"]!.GetValue<string>(), StringComparison.Ordinal);

        var failed = WebhookSender.Payload(new ConnectEvent { Type = ConnectEventType.DownloadFailure, Message = "Not enough repair blocks", DownloadId = 4 }, "x");
        Assert.Equal("DownloadFailure", failed["eventType"]!.GetValue<string>());
        Assert.Equal("Not enough repair blocks", failed["message"]!.GetValue<string>());

        Assert.Equal("Test", WebhookSender.Payload(ConnectEvent.Test(), "x")["eventType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Email_builds_the_message_and_passes_server_settings()
    {
        var transport = new StubTransport();
        var context = ConnectTestData.Context("email", new
        {
            server = "smtp.example.com",
            port = 465,
            useEncryption = "always",
            username = "me",
            password = "pw",
            from = "StreamZH <streamzh@example.com>",
            to = "a@example.com, b@example.com",
            cc = "c@example.com",
            bcc = "d@example.com",
        });

        await new EmailSender(transport).SendAsync(context, ConnectTestData.MovieImport(), default);

        var (server, message) = Assert.Single(transport.Sent);
        Assert.Equal(new EmailServer("smtp.example.com", 465, "always", "me", "pw"), server);
        Assert.Equal(MailKit.Security.SecureSocketOptions.SslOnConnect, MailKitTransport.Options(server));
        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTls, MailKitTransport.Options(server with { Port = 587 }));
        Assert.Equal(MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable, MailKitTransport.Options(server with { Port = 587, Encryption = "preferred" }));
        Assert.Equal(MailKit.Security.SecureSocketOptions.None, MailKitTransport.Options(server with { Encryption = "never" }));
        Assert.Equal("StreamZH - Movie Downloaded: Dune (2021)", message.Subject);
        Assert.Equal(["a@example.com", "b@example.com"], message.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("c@example.com", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Equal("d@example.com", Assert.Single(message.Bcc.Mailboxes).Address);
        Assert.Equal("streamzh@example.com", Assert.Single(message.From.Mailboxes).Address);
        Assert.Contains("https://www.themoviedb.org/movie/438631", message.TextBody, StringComparison.Ordinal);
        Assert.EndsWith("My StreamZH", message.TextBody!.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Email_addresses_are_validated()
    {
        var errors = new Dictionary<string, string[]>();
        var settings = ConnectTestData.Context("email", new { server = "smtp", from = "not an address", to = "ok@example.com, nope" }).Settings;

        new EmailSender(new StubTransport()).Validate(settings, errors);

        Assert.True(errors.ContainsKey("from"));
        Assert.Equal(["nope is not an email address."], errors["to"]);
    }

    [Fact]
    public async Task Email_reaches_a_real_smtp_server_with_login()
    {
        using var smtp = new FakeSmtpServer();
        var context = ConnectTestData.Context("email", new { server = "127.0.0.1", port = smtp.Port, useEncryption = "never", username = "me", password = "secret", from = "s@example.com", to = "r@example.com" });

        var message = await new EmailSender(new MailKitTransport()).TestAsync(context, default);

        Assert.Equal("Test email sent.", message);
        Assert.Contains("AUTH PLAIN " + Convert.ToBase64String("\0me\0secret"u8.ToArray()), smtp.Commands);
        Assert.Contains(smtp.Commands, c => c.StartsWith("MAIL FROM:<s@example.com>", StringComparison.Ordinal));
        Assert.Contains("RCPT TO:<r@example.com>", smtp.Commands);
        Assert.Contains("Subject: StreamZH - Test Notification: Big Buck Bunny (2008)", smtp.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Email_wrong_password_is_unauthorized()
    {
        using var smtp = new FakeSmtpServer { RejectLogin = true };
        var context = ConnectTestData.Context("email", new { server = "127.0.0.1", port = smtp.Port, useEncryption = "never", username = "me", password = "bad", from = "s@example.com", to = "r@example.com" });

        var ex = await Assert.ThrowsAsync<ConnectException>(() => new EmailSender(new MailKitTransport()).TestAsync(context, default));

        Assert.StartsWith("unauthorized", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.Retryable);
    }

    private sealed class StubTransport : IEmailTransport
    {
        public List<(EmailServer Server, MimeMessage Message)> Sent { get; } = [];

        public Task SendAsync(EmailServer server, MimeMessage message, CancellationToken cancellationToken)
        {
            Sent.Add((server, message));
            return Task.CompletedTask;
        }
    }
}
