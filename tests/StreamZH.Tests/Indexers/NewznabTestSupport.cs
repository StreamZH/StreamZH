// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Text;
using StreamZH.Core.Indexers;
using StreamZH.Core.Net;
using StreamZH.Indexers.Newznab;

namespace StreamZH.Tests.Indexers;

internal static class NewznabTestSupport
{
    public const string ApiKey = "abcd1234secret";

    public static IndexerDefinition Indexer(
        string baseUrl = "http://127.0.0.1:1234/",
        string apiPath = "/api",
        string apiKey = ApiKey,
        IReadOnlyList<int>? categories = null,
        IReadOnlyList<int>? tvCategories = null) =>
        new(7, "TestIndexer", IndexerDefinition.Newznab, baseUrl, apiPath, apiKey,
            categories ?? [2000, 2040], tvCategories ?? [5000, 5040], 12, true, true, true);

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Indexers", "Files", name));

    public static byte[] FixtureBytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Indexers", "Files", name));

    /// <summary>The raw (still escaped) query parameters in order; repeated keys keep their first value.</summary>
    public static Dictionary<string, string> Query(Uri url)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            var key = index < 0 ? part : part[..index];
            result.TryAdd(key, index < 0 ? string.Empty : part[(index + 1)..]);
        }

        return result;
    }

    /// <summary>A feed with <paramref name="count"/> items named g{start}..; total is sent when given.</summary>
    public static string Feed(int count, int start = 0, int? total = null, string prefix = "g")
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel>""");
        if (total is { } t)
        {
            builder.Append($"""<newznab:response offset="{start}" total="{t}" />""");
        }

        for (var i = start; i < start + count; i++)
        {
            builder.Append($"""
                <item><title>Release.{prefix}{i}.2020.1080p.WEB-DL-GRP</title><guid>{prefix}{i}</guid>
                <pubDate>Mon, 27 Feb 2012 11:09:39 +0000</pubDate>
                <enclosure url="http://127.0.0.1:1234/getnzb/{prefix}{i}.nzb" length="1000" type="application/x-nzb" /></item>
                """);
        }

        return builder.Append("</channel></rss>").ToString();
    }

    public static string Caps(string movieSearch = """<movie-search available="yes" supportedParams="q,imdbid,tmdbid" />""",
        string tvSearch = """<tv-search available="yes" supportedParams="q,tvdbid,season,ep" />""",
        string search = """<search available="yes" supportedParams="q" />""",
        string limits = """<limits max="100" default="100" />""") =>
        $"""<?xml version="1.0" encoding="UTF-8"?><caps>{limits}<searching>{search}{tvSearch}{movieSearch}</searching><categories /></caps>""";

    public static NewznabClient Client(StubIndexer handler, NewznabCapabilitiesCache? cache = null, OutboundAddressGuard? guard = null) =>
        new(new HttpClient(handler), cache ?? new NewznabCapabilitiesCache(), guard: guard ?? OfflineGuard);

    /// <summary>The outbound guard without real DNS: "localhost" is loopback, every other name does not resolve.</summary>
    public static OutboundAddressGuard OfflineGuard { get; } = new((host, _) => host == "localhost"
        ? Task.FromResult(new[] { IPAddress.Loopback })
        : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));

    public static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/rss+xml") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}

/// <summary>Plays the indexer: answers t=caps with <see cref="CapsXml"/>, everything else via <see cref="Respond"/>, and records every URL.</summary>
internal sealed class StubIndexer : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public string CapsXml { get; set; } = NewznabTestSupport.Caps();

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => NewznabTestSupport.Xml(NewznabTestSupport.Feed(0));

    /// <summary>Requests other than t=caps.</summary>
    public List<Uri> ApiRequests => [.. Requests.Where(r => !r.Query.Contains("t=caps", StringComparison.Ordinal))];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        return Task.FromResult(request.RequestUri!.Query.Contains("t=caps", StringComparison.Ordinal)
            ? NewznabTestSupport.Xml(CapsXml, mediaType: "application/xml")
            : Respond(request));
    }
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
