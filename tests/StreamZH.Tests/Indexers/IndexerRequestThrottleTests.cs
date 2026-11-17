// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Net;
using StreamZH.Core.Indexers;
using StreamZH.Indexers;
using StreamZH.Indexers.Newznab;

namespace StreamZH.Tests.Indexers;

public sealed class IndexerRequestThrottleTests
{
    [Fact]
    public void The_interval_comes_from_the_requests_per_second()
    {
        var indexer = NewznabTestSupport.Indexer();
        Assert.Equal(TimeSpan.FromMilliseconds(500), indexer.RequestInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(200), (indexer with { RequestsPerSecond = 5 }).RequestInterval);
        Assert.Null((indexer with { RequestsPerSecond = 0 }).RequestInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(50), (indexer with { RequestsPerSecond = 100 }).RequestInterval);
    }

    [Fact]
    public async Task Requests_sent_side_by_side_are_spaced_out()
    {
        var throttle = new IndexerRequestThrottle();
        var gap = TimeSpan.FromMilliseconds(80);
        var watch = Stopwatch.StartNew();
        var started = new List<long>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            Assert.Null(await throttle.AcquireAsync("indexer.example:443", gap, CancellationToken.None));
            lock (started)
            {
                started.Add(watch.ElapsedMilliseconds);
            }
        }));

        started.Sort();
        Assert.True(started[^1] >= 3 * 80 - 20, $"last request after {started[^1]} ms");

        // Another host has its own slots.
        watch.Restart();
        Assert.Null(await throttle.AcquireAsync("other.example:443", gap, CancellationToken.None));
        Assert.True(watch.ElapsedMilliseconds < 60);
    }

    [Fact]
    public async Task A_held_host_sends_nothing_until_the_hold_ends()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero));
        var throttle = new IndexerRequestThrottle(time);
        throttle.Hold("indexer.example:443", TimeSpan.FromMinutes(10));
        throttle.Hold("indexer.example:443", TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(10), await throttle.AcquireAsync("indexer.example:443", null, CancellationToken.None));
        Assert.Null(await throttle.AcquireAsync("other.example:443", null, CancellationToken.None));

        time.Now += TimeSpan.FromMinutes(10);
        Assert.Null(await throttle.AcquireAsync("indexer.example:443", null, CancellationToken.None));
    }

    [Fact]
    public async Task The_off_throttle_neither_waits_nor_holds()
    {
        IndexerRequestThrottle.Off.Hold("indexer.example:443", TimeSpan.FromHours(1));
        Assert.Null(await IndexerRequestThrottle.Off.AcquireAsync("indexer.example:443", TimeSpan.FromHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task After_a_429_the_client_sends_no_more_requests_to_the_indexer()
    {
        var handler = new StubIndexer
        {
            Respond = _ =>
            {
                var response = NewznabTestSupport.Xml("Too many requests", HttpStatusCode.TooManyRequests, "text/plain");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return response;
            },
        };
        var throttle = new IndexerRequestThrottle();
        var client = new NewznabClient(new HttpClient(handler), new NewznabCapabilitiesCache(), guard: NewznabTestSupport.OfflineGuard, throttle: throttle);
        var indexer = NewznabTestSupport.Indexer() with { RequestsPerSecond = 0 };

        var first = await Assert.ThrowsAsync<IndexerException>(() => client.FetchRecentAsync(indexer, CancellationToken.None));
        Assert.False(first.IsLimitReached);
        Assert.Equal(TimeSpan.FromMinutes(2), first.RetryAfter);
        var sent = handler.Requests.Count;

        var second = await Assert.ThrowsAsync<IndexerException>(() => client.FetchRecentAsync(indexer, CancellationToken.None));
        Assert.True(second.IsLimitReached);
        Assert.StartsWith("TestIndexer: request limit reached, StreamZH sends the next request in ", second.Message);
        Assert.Equal(sent, handler.Requests.Count);
    }
}
