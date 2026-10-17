// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using StreamZH.Core.Media;
using StreamZH.Data;
using StreamZH.Library.Notifications;

namespace StreamZH.Tests.Connect;

/// <summary>A recorded request with its body read already (the content is gone after sending).</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>Fake HTTP server for the connection senders: answers by a function, records every request.</summary>
public sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>Status and body per request; default 200 with "{}".</summary>
    public Func<RecordedRequest, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.OK, "{}");

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public RecordedRequest Last => Requests[^1];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        _requests.Enqueue(recorded);
        var (status, answer) = Respond(recorded);
        return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }
}

public static class ConnectTestData
{
    /// <summary>The connection context for a sender test: settings of the type's defaults plus <paramref name="settings"/>.</summary>
    public static ConnectionContext Context(string implementation, object settings, int id = 7, string name = "Test")
    {
        var type = ConnectCatalog.Find(implementation)!;
        var input = JsonSerializer.SerializeToElement(settings).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var errors = new Dictionary<string, string[]>();
        var values = ConnectionSettings.Normalize(type, input, ConnectionSettings.Defaults(type), errors);
        Assert.Empty(errors);
        var definition = new ConnectionDefinition(id, name, implementation, true, new ConnectionTriggers(true, true, true, true, true, true, true, true), values);
        return new ConnectionContext(definition, new ConnectionSettings(values), "My StreamZH");
    }

    public static ConnectEvent MovieImport(bool upgrade = false, string folder = "/media/movies/Dune (2021)") => new()
    {
        Type = upgrade ? ConnectEventType.Upgrade : ConnectEventType.Import,
        MediaType = MediaType.Movie,
        Media = new ConnectMedia(3, "Dune", 2021, 438631, "tt1160419", null, "Paul Atreides...", ["Science Fiction"], "/dune.jpg", "/dune-back.jpg", folder),
        Release = new ConnectRelease("Dune.2021.1080p.WEB-DL.DDP5.1.H.264-FLUX", "WEBDL-1080p", "FLUX", null, 5_000_000_000),
        Files = [new ConnectFile(folder + "/Dune (2021).mkv", "Dune (2021).mkv", 5_000_000_000)],
        Folder = folder,
        DownloadId = 12,
    };

    public static ConnectEvent EpisodeGrab() => new()
    {
        Type = ConnectEventType.Grab,
        MediaType = MediaType.Series,
        Media = new ConnectMedia(5, "Breaking Bad", 2008, 1396, "tt0903747", 81189, "Walter White...", ["Drama"], "/bb.jpg", null, "/media/tv/Breaking Bad"),
        Episodes = [new ConnectEpisode(41, 1, 2, "Cat's in the Bag...", "2008-01-27", null)],
        Release = new ConnectRelease("Breaking.Bad.S01E02.1080p.BluRay.x264-ROVERS", "Bluray-1080p", "ROVERS", "NZBgeek", 1_500_000_000),
    };

    public static ConnectEvent Health(string level = "warn") => new()
    {
        Type = ConnectEventType.HealthIssue,
        Health = new ConnectHealth("no-indexer", level, "No indexer configured", "StreamZH cannot search for releases without an indexer.", "/indexers"),
    };
}
