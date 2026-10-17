// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Tests.Library;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Usenet;

/// <summary>STAT for the download engine's pre-check: by priority like BODY, without downloading, also between pipelined BODYs.</summary>
public sealed class UsenetStatTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakeNntpServer _primary = new() { Username = "user", Password = "pass" };
    private readonly FakeNntpServer _backup = new();
    private UsenetArticleSource _source = null!;

    public Task InitializeAsync()
    {
        _source = new UsenetArticleSource(
            _time, NullLogger<UsenetArticleSource>.Instance, new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(5) });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _primary.DisposeAsync();
        await _backup.DisposeAsync();
    }

    [Fact]
    public async Task Stat_asks_the_servers_by_priority_and_downloads_nothing()
    {
        _source.Configure([Server(1, _primary, 0, "user", "pass"), Server(2, _backup, 1)]);
        _primary.AddYenc("here@x", [1, 2, 3]);
        _backup.AddYenc("backup@x", [4, 5, 6]);
        IArticleChecker checker = _source;

        Assert.True(await checker.ExistsAsync("here@x", Start, CancellationToken.None));
        Assert.True(await checker.ExistsAsync("<backup@x>", Start, CancellationToken.None));
        Assert.False(await checker.ExistsAsync("gone@x", Start, CancellationToken.None));

        Assert.Equal(0, _primary.BodyRequests + _backup.BodyRequests);
        Assert.Contains("STAT <here@x>", _primary.Commands);
        Assert.DoesNotContain("STAT <here@x>", _backup.Commands);
        Assert.Contains("STAT <backup@x>", _backup.Commands);
    }

    [Fact]
    public async Task Stat_and_body_share_a_pipelined_connection_in_order()
    {
        _source.Configure([Server(1, _primary, 0, "user", "pass", connections: 1, articlesPerRequest: 4)]);
        for (var i = 0; i < 20; i++)
        {
            _primary.AddYenc($"a{i}@x", [(byte)i, 1, 2]);
        }

        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            if (i % 2 == 0)
            {
                var article = await _source.FetchAsync($"a{i / 2}@x", [], Start, CancellationToken.None);
                Assert.Equal((byte)(i / 2), article.Data[0]);
                return true;
            }

            return await _source.ExistsAsync(i % 4 == 1 ? $"a{i / 2}@x" : $"missing{i}@x", Start, CancellationToken.None) == (i % 4 == 1);
        });

        Assert.All(await Task.WhenAll(tasks), Assert.True);
        Assert.Equal(1, _primary.ConnectionsOpened);
    }

    [Fact]
    public async Task Without_a_reachable_server_stat_throws()
    {
        await Assert.ThrowsAsync<UsenetServerException>(() => _source.ExistsAsync("x@x", Start, CancellationToken.None));

        _source.Configure([Server(1, _primary, 0, "user", "wrong")]);
        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.ExistsAsync("x@x", Start, CancellationToken.None));
        Assert.Contains("Login failed", error.Message);
    }

    private static UsenetServer Server(int id, FakeNntpServer server, int priority, string user = "", string password = "", int connections = 2, int articlesPerRequest = 1) =>
        new(id, $"S{id}", "127.0.0.1", server.Port, false, user, password, connections, priority, true, 0, ArticlesPerRequest: articlesPerRequest);
}
