// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace StreamZH.Tests.Library.SceneNumbering;

/// <summary>
/// TheXEM stand-in: answers by path ("map/havemap", "map/all", "map/allNames") from <see cref="Responses"/>; unknown paths
/// get TheXEM's "failure" answer. <see cref="Down"/> makes every request fail like an unreachable server.
/// </summary>
public sealed class FakeXem : HttpMessageHandler
{
    public const string Failure = """{"result":"failure","data":[],"message":"no show with the tvdb_id found"}""";

    /// <summary>By default TheXEM knows no series and no names.</summary>
    public ConcurrentDictionary<string, string> Responses { get; } = new(StringComparer.Ordinal)
    {
        ["map/havemap"] = """{"result":"success","data":[],"message":""}""",
        ["map/allNames"] = """{"result":"success","data":{},"message":""}""",
    };

    public ConcurrentQueue<string> Requests { get; } = new();

    public bool Down { get; set; }

    public int Count(string path) => Requests.Count(r => r.StartsWith(path, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri!.PathAndQuery.TrimStart('/');
        Requests.Enqueue(pathAndQuery);
        if (Down)
        {
            throw new HttpRequestException("Connection refused (thexem.info:443)");
        }

        var path = request.RequestUri.AbsolutePath.TrimStart('/');
        var body = Responses.TryGetValue(pathAndQuery, out var exact) ? exact
            : Responses.TryGetValue(path, out var byPath) ? byPath
            : Failure;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
