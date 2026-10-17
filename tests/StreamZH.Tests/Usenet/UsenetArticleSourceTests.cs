// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Tests.Library;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Usenet;

public sealed class UsenetArticleSourceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakeNntpServer _primary = new() { Username = "user", Password = "pass" };
    private readonly FakeNntpServer _backup = new() { Username = "block", Password = "pass2" };
    private UsenetArticleSource _source = null!;

    public Task InitializeAsync()
    {
        _source = NewSource();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _primary.DisposeAsync();
        await _backup.DisposeAsync();
    }

    [Fact]
    public async Task Fetches_from_the_primary_and_reuses_the_connection()
    {
        _source.Configure([Primary()]);
        for (var i = 0; i < 5; i++)
        {
            _primary.AddYenc($"a{i}@x", [(byte)i, 1, 2]);
        }

        for (var i = 0; i < 5; i++)
        {
            var article = await _source.FetchAsync($"a{i}@x", ["alt.binaries.test"], Start, CancellationToken.None);
            Assert.Equal([(byte)i, 1, 2], article.Data);
        }

        Assert.Equal(1, _primary.ConnectionsOpened);
        Assert.Equal(0, _backup.ConnectionsOpened);
    }

    [Fact]
    public async Task Missing_article_goes_to_the_next_server_by_priority()
    {
        // Given in the "wrong" order: priority decides, not the list order.
        _source.Configure([Backup(priority: 5), Primary()]);
        _backup.AddYenc("fill@x", [7, 7, 7]);

        var article = await _source.FetchAsync("fill@x", [], Start, CancellationToken.None);

        Assert.Equal([7, 7, 7], article.Data);
        Assert.Equal(1, _primary.BodyRequests);
        Assert.Equal(1, _backup.BodyRequests);
    }

    [Fact]
    public async Task Not_found_on_any_server_is_article_not_found()
    {
        _source.Configure([Primary(), Backup()]);

        var error = await Assert.ThrowsAsync<ArticleNotFoundException>(() => _source.FetchAsync("<nowhere@x>", [], Start, CancellationToken.None));

        Assert.Equal("nowhere@x", error.MessageId);
        Assert.Equal(1, _primary.BodyRequests);
        Assert.Equal(1, _backup.BodyRequests);
    }

    [Fact]
    public async Task Articles_older_than_the_retention_skip_that_server()
    {
        _source.Configure([Primary() with { RetentionDays = 10 }, Backup()]);
        _primary.AddYenc("old@x", [1]);
        _backup.AddYenc("old@x", [2]);

        var article = await _source.FetchAsync("old@x", [], Start.AddDays(-30), CancellationToken.None);

        Assert.Equal([2], article.Data);
        Assert.Equal(0, _primary.BodyRequests);

        // Within the retention the primary is asked.
        Assert.Equal([1], (await _source.FetchAsync("old@x", [], Start.AddDays(-3), CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Too_old_for_every_server_is_article_not_found_without_asking()
    {
        _source.Configure([Primary() with { RetentionDays = 10 }]);
        _primary.AddYenc("old@x", [1]);

        await Assert.ThrowsAsync<ArticleNotFoundException>(() => _source.FetchAsync("old@x", [], Start.AddDays(-11), CancellationToken.None));
        Assert.Equal(0, _primary.ConnectionsOpened);
    }

    [Fact]
    public async Task Broken_yenc_on_one_server_is_taken_from_the_next()
    {
        _source.Configure([Primary(), Backup()]);
        _primary.Add("broken@x", "=ybegin part=1 line=128 size=10 name=x\r\nabc\r\n"u8.ToArray());
        _backup.AddYenc("broken@x", [3, 3]);

        Assert.Equal([3, 3], (await _source.FetchAsync("broken@x", [], Start, CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Retries_on_another_connection_after_a_dropped_connection()
    {
        _source.Configure([Primary()]);
        _primary.AddYenc("flaky@x", YencArticles.Random(20_000));
        _primary.DropNextBodies(2);

        var article = await _source.FetchAsync("flaky@x", [], Start, CancellationToken.None);

        Assert.True(article.CrcOk);
        Assert.Equal(3, _primary.BodyRequests);
        Assert.Equal(3, _primary.ConnectionsOpened);
        Assert.NotNull(_source.Status().Servers.Single().LastError);
        Assert.True(_source.Status().Servers.Single().Healthy);
    }

    [Fact]
    public async Task After_three_transient_failures_the_next_server_is_asked()
    {
        _source.Configure([Primary(), Backup()]);
        _primary.AddYenc("flaky@x", [1]);
        _backup.AddYenc("flaky@x", [2]);
        _primary.DropNextBodies(3);

        Assert.Equal([2], (await _source.FetchAsync("flaky@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal(3, _primary.BodyRequests);
    }

    [Fact]
    public async Task Only_transient_failures_everywhere_is_a_server_error()
    {
        _source.Configure([Primary()]);
        _primary.AddYenc("flaky@x", [1]);
        _primary.DropNextBodies(3);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("flaky@x", [], Start, CancellationToken.None));

        Assert.False(error.IsAuthError);
    }

    [Fact]
    public async Task Refused_login_backs_off_for_five_minutes_and_is_an_auth_error_when_all_fail()
    {
        _source.Configure([Primary() with { Password = "wrong" }]);
        _primary.AddYenc("a@x", [1]);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.True(error.IsAuthError);
        Assert.Contains("481", error.Message, StringComparison.Ordinal);

        var status = _source.Status().Servers.Single();
        Assert.False(status.Healthy);
        Assert.Equal(Start.AddMinutes(5), status.RetryAt);
        Assert.Contains("481", status.LastError, StringComparison.Ordinal);

        // Skipped without connecting while in back-off.
        var opened = _primary.ConnectionsOpened;
        Assert.True((await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None))).IsAuthError);
        Assert.Equal(opened, _primary.ConnectionsOpened);

        // Tried again after the back-off (and the fixed password brings it back).
        _primary.Password = "wrong";
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal([1], (await _source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);
        status = _source.Status().Servers.Single();
        Assert.True(status.Healthy);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task Refused_login_on_the_primary_uses_the_backup()
    {
        _source.Configure([Primary() with { Password = "wrong" }, Backup()]);
        _backup.AddYenc("a@x", [5]);

        Assert.Equal([5], (await _source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);

        var servers = _source.Status().Servers;
        Assert.False(servers[0].Healthy);
        Assert.True(servers[1].Healthy);
    }

    [Fact]
    public async Task Missing_on_one_server_while_the_other_fails_is_article_not_found()
    {
        _source.Configure([Primary() with { Password = "wrong" }, Backup()]);

        await Assert.ThrowsAsync<ArticleNotFoundException>(() => _source.FetchAsync("nowhere@x", [], Start, CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_server_is_a_server_error_and_backs_off()
    {
        var port = _backup.Port;
        await _backup.DisposeAsync();
        _source.Configure([Backup() with { Port = port }]);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));

        Assert.False(error.IsAuthError);
        Assert.False(_source.Status().Servers.Single().Healthy);
    }

    [Fact]
    public async Task Never_uses_more_connections_than_configured()
    {
        _source.Configure([Primary() with { Connections = 3 }]);
        _primary.BodyDelay = TimeSpan.FromMilliseconds(80);
        for (var i = 0; i < 12; i++)
        {
            _primary.AddYenc($"p{i}@x", [(byte)i]);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => _source.FetchAsync($"p{i}@x", [], Start, CancellationToken.None)));

        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), results.Select(r => r.Data[0]));
        Assert.Equal(3, _primary.MaxConcurrentConnections);
        Assert.Equal(3, _primary.ConnectionsOpened);
        Assert.Equal(0, _source.Status().Servers.Single().ConnectionsInUse);
    }

    [Fact]
    public async Task Shows_connections_in_use_while_fetching()
    {
        _source.Configure([Primary() with { Connections = 2 }]);
        _primary.BodyDelay = TimeSpan.FromMilliseconds(500);
        _primary.AddYenc("slow@x", [1]);

        var fetch = _source.FetchAsync("slow@x", [], Start, CancellationToken.None);
        await WaitUntil(() => _primary.BodyRequests == 1);

        Assert.Equal(1, _source.Status().Servers.Single().ConnectionsInUse);
        await fetch;
        Assert.Equal(0, _source.Status().Servers.Single().ConnectionsInUse);
    }

    [Fact]
    public async Task Cancellation_frees_the_slot()
    {
        _source.Configure([Primary() with { Connections = 1 }]);
        _primary.AddYenc("slow@x", [1]);
        _primary.AddYenc("fast@x", [2]);
        _primary.StallBodies = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.FetchAsync("slow@x", [], Start, cancel.Token));

        _primary.StallBodies = false;
        Assert.Equal([2], (await _source.FetchAsync("fast@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal(2, _primary.ConnectionsOpened); // the cancelled connection was not reused
    }

    [Fact]
    public async Task Waiting_for_a_slot_can_be_cancelled()
    {
        _source.Configure([Primary() with { Connections = 1 }]);
        _primary.AddYenc("slow@x", [1]);
        _primary.BodyDelay = TimeSpan.FromSeconds(1);

        var first = _source.FetchAsync("slow@x", [], Start, CancellationToken.None);
        await WaitUntil(() => _primary.BodyRequests == 1);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.FetchAsync("slow@x", [], Start, cancel.Token));

        Assert.Equal([1], (await first).Data);
        Assert.Equal(1, _primary.BodyRequests);
    }

    [Fact]
    public async Task Counts_bytes_per_server_and_speed()
    {
        _source.Configure([Primary(), Backup()]);
        _primary.AddYenc("big@x", YencArticles.Random(100_000));

        await _source.FetchAsync("big@x", [], Start, CancellationToken.None);

        var status = _source.Status();
        Assert.True(status.Servers[0].BytesToday > 100_000);
        Assert.Equal(0, status.Servers[1].BytesToday);
        Assert.True(status.SpeedBps > 0);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, _source.Status().SpeedBps);
        Assert.Equal(status.Servers[0].BytesToday, _source.BytesToday(1));
    }

    [Fact]
    public void Max_connections_is_the_sum_of_enabled_servers()
    {
        Assert.Equal(0, _source.MaxConnections);
        _source.Configure([Primary() with { Connections = 30 }, Backup() with { Connections = 10 }, Backup() with { Id = 3, Enabled = false }]);

        Assert.Equal(40, _source.MaxConnections);
        Assert.Equal([1, 2], _source.Status().Servers.Select(s => s.Id));
    }

    [Fact]
    public async Task No_enabled_server_is_a_server_error()
    {
        _source.Configure([Primary() with { Enabled = false }]);

        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
    }

    [Fact]
    public async Task Idle_connections_are_closed_after_a_minute()
    {
        _source.Configure([Primary()]);
        _primary.AddYenc("a@x", [1]);
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        Assert.Equal(1, _primary.CurrentConnections);

        _time.Advance(TimeSpan.FromSeconds(30));
        _source.CloseIdleConnections();
        Assert.Equal(1, _primary.CurrentConnections);

        _time.Advance(TimeSpan.FromSeconds(31));
        _source.CloseIdleConnections();
        await WaitUntil(() => _primary.CurrentConnections == 0);
        Assert.Contains("QUIT", _primary.Commands);

        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        Assert.Equal(2, _primary.ConnectionsOpened);
    }

    [Fact]
    public async Task Reconfiguring_keeps_unchanged_servers_and_replaces_changed_ones()
    {
        _source.Configure([Primary()]);
        _primary.AddYenc("a@x", [1]);
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);

        _source.Configure([Primary() with { Name = "Renamed", RetentionDays = 4000 }]);
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        Assert.Equal(1, _primary.ConnectionsOpened);
        Assert.Equal("Renamed", _source.Status().Servers.Single().Name);

        _source.Configure([Primary() with { Connections = 5 }]);
        await WaitUntil(() => _primary.CurrentConnections == 0);
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        Assert.Equal(2, _primary.ConnectionsOpened);

        _source.Configure([]);
        await WaitUntil(() => _primary.CurrentConnections == 0);
        Assert.Equal(0, _source.MaxConnections);
    }

    [Fact]
    public async Task Works_over_tls()
    {
        using var certificate = FakeNntpServer.CreateCertificate();
        await using var server = new FakeNntpServer(certificate);
        await using var source = NewSource(FakeNntpServer.Trust(certificate));
        source.Configure([new UsenetServer(9, "TLS", "localhost", server.Port, true, string.Empty, string.Empty, 4, 0, true, 0)]);
        server.AddYenc("tls@x", YencArticles.Random(1000));

        Assert.True((await source.FetchAsync("tls@x", [], Start, CancellationToken.None)).CrcOk);
    }

    private UsenetArticleSource NewSource(System.Net.Security.RemoteCertificateValidationCallback? validation = null) => new(
        _time,
        NullLogger<UsenetArticleSource>.Instance,
        new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(5), CertificateValidation = validation });

    private UsenetServer Primary() => new(1, "Primary", "127.0.0.1", _primary.Port, false, "user", "pass", 4, 0, true, 0);

    private UsenetServer Backup(int priority = 1) => new(2, "Backup", "127.0.0.1", _backup.Port, false, "block", "pass2", 2, priority, true, 0);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}
