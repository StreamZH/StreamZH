// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Security;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Tests.Library;
using StreamZH.Usenet.Nntp;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Usenet;

/// <summary>The expert settings of a server in the engine: pipelining, required/optional, quota, timeout, certificate check, tries.</summary>
public sealed class UsenetServerExpertTests : IAsyncLifetime
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

    // ---- Articles per request (pipelining) ----

    [Fact]
    public async Task Pipelining_sends_several_bodies_on_one_connection_before_the_first_answer()
    {
        _source.Configure([Primary() with { Connections = 1, ArticlesPerRequest = 4 }]);
        for (var i = 0; i < 4; i++)
        {
            _primary.AddYenc($"p{i}@x", YencArticles.Random(5000 + i, seed: i));
        }

        _primary.BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = Enumerable.Range(0, 4).Select(i => _source.FetchAsync($"p{i}@x", [], Start, CancellationToken.None)).ToList();

        // All four commands are on the wire while the server has not answered the first one.
        await WaitUntil(() => _primary.BodiesReceived == 4);
        Assert.Equal(1, _primary.BodyRequests);
        Assert.Equal(1, _source.Status().Servers.Single().ConnectionsInUse);

        _primary.BodyGate.SetResult();
        var articles = await Task.WhenAll(fetches);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(YencArticles.Random(5000 + i, seed: i), articles[i].Data);
        }

        Assert.Equal(1, _primary.ConnectionsOpened);
        Assert.Equal(0, _source.Status().Servers.Single().ConnectionsInUse);
    }

    [Fact]
    public async Task Without_pipelining_one_body_at_a_time()
    {
        _source.Configure([Primary() with { Connections = 1 }]);
        _primary.AddYenc("a@x", [1]);
        _primary.AddYenc("b@x", [2]);
        _primary.BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        var second = _source.FetchAsync("b@x", [], Start, CancellationToken.None);
        await WaitUntil(() => _primary.BodiesReceived == 1);
        await Task.Delay(100);
        Assert.Equal(1, _primary.BodiesReceived);

        _primary.BodyGate.SetResult();
        Assert.Equal([1], (await first).Data);
        Assert.Equal([2], (await second).Data);
    }

    [Fact]
    public async Task Pipelining_keeps_answers_in_order_with_a_missing_article_in_between()
    {
        _source.Configure([Primary() with { Connections = 1, ArticlesPerRequest = 3 }]);
        _primary.AddYenc("a@x", [1, 1]);
        _primary.AddYenc("c@x", [3, 3, 3]);
        _primary.BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        var b = _source.FetchAsync("b@x", [], Start, CancellationToken.None);
        var c = _source.FetchAsync("c@x", [], Start, CancellationToken.None);
        await WaitUntil(() => _primary.BodiesReceived == 3);
        _primary.BodyGate.SetResult();

        Assert.Equal([1, 1], (await a).Data);
        await Assert.ThrowsAsync<ArticleNotFoundException>(() => b);
        Assert.Equal([3, 3, 3], (await c).Data);
        Assert.Equal(1, _primary.ConnectionsOpened);
    }

    [Fact]
    public async Task Pipelining_retries_everything_behind_a_dropped_answer_on_a_new_connection()
    {
        _source.Configure([Primary() with { Connections = 2, ArticlesPerRequest = 4 }]);
        for (var i = 0; i < 8; i++)
        {
            _primary.AddYenc($"d{i}@x", YencArticles.Random(20_000, seed: 10 + i));
        }

        _primary.DropNextBodies(1);
        var articles = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _source.FetchAsync($"d{i}@x", [], Start, CancellationToken.None)));

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(YencArticles.Random(20_000, seed: 10 + i), articles[i].Data);
        }

        Assert.True(_primary.ConnectionsOpened >= 3);
        Assert.True(_primary.MaxConcurrentConnections <= 2);
    }

    [Fact]
    public async Task A_cancelled_pipelined_fetch_does_not_mix_up_the_answers()
    {
        _source.Configure([Primary() with { Connections = 1, ArticlesPerRequest = 2 }]);
        _primary.AddYenc("a@x", [1]);
        _primary.AddYenc("b@x", [2]);
        _primary.AddYenc("c@x", [3]);
        _primary.BodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var a = _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        var b = _source.FetchAsync("b@x", [], Start, cancel.Token);
        await WaitUntil(() => _primary.BodiesReceived == 2);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);

        _primary.BodyGate.SetResult();
        Assert.Equal([1], (await a).Data);

        // The connection carrying b's unread answer is not reused.
        Assert.Equal([3], (await _source.FetchAsync("c@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal(2, _primary.ConnectionsOpened);
    }

    [Fact]
    public void Max_connections_counts_the_articles_per_request()
    {
        _source.Configure([Primary() with { Connections = 10, ArticlesPerRequest = 2 }, Backup() with { Connections = 4 }]);

        Assert.Equal(24, _source.MaxConnections);
    }

    // ---- Required and optional servers ----

    [Fact]
    public async Task A_required_server_that_refuses_the_login_pauses_instead_of_using_the_backup()
    {
        _source.Configure([Primary() with { Password = "wrong", OnFailure = ServerFailureAction.PauseDownloads }, Backup()]);
        _backup.AddYenc("a@x", [5]);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.True(error.IsAuthError);
        Assert.Contains("Primary is required", error.Message, StringComparison.Ordinal);

        // In back-off: refused right away, without asking any server.
        var opened = _primary.ConnectionsOpened;
        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.Equal(opened, _primary.ConnectionsOpened);
        Assert.Equal(0, _backup.BodyRequests);

        // Working again after the back-off: downloads continue.
        _primary.AddYenc("a@x", [1]);
        _primary.Password = "wrong";
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal([1], (await _source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);
    }

    [Fact]
    public async Task A_required_server_with_errors_does_not_let_the_backup_mark_the_article_missing()
    {
        _source.Configure([Primary() with { OnFailure = ServerFailureAction.PauseDownloads }, Backup()]);
        _primary.AddYenc("a@x", [1]);
        _primary.DropNextBodies(3);

        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.Equal(0, _backup.BodyRequests);

        // The same with a normal server: the backup's 430 makes it missing.
        _source.Configure([Primary(), Backup()]);
        _primary.DropNextBodies(3);
        await Assert.ThrowsAsync<ArticleNotFoundException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
    }

    [Fact]
    public async Task A_required_backup_still_only_fills_gaps_while_it_works()
    {
        _source.Configure([Primary(), Backup() with { OnFailure = ServerFailureAction.PauseDownloads }]);
        _primary.AddYenc("a@x", [1]);
        _backup.AddYenc("b@x", [2]);

        Assert.Equal([1], (await _source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal([2], (await _source.FetchAsync("b@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal(1, _backup.BodyRequests);
    }

    [Fact]
    public async Task An_optional_server_is_skipped_for_thirty_minutes()
    {
        _source.Configure([Primary(), Backup() with { Password = "wrong", OnFailure = ServerFailureAction.IgnoreLonger }]);
        _primary.AddYenc("a@x", [1]);

        await Assert.ThrowsAsync<ArticleNotFoundException>(() => _source.FetchAsync("missing@x", [], Start, CancellationToken.None));

        var backup = _source.Status().Servers.Single(s => s.Id == 2);
        Assert.False(backup.Healthy);
        Assert.Equal(Start.AddMinutes(30), backup.RetryAt);
        Assert.Equal([1], (await _source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);
    }

    [Fact]
    public async Task Health_changes_raise_state_changed()
    {
        var changes = 0;
        _source.StateChanged += () => Interlocked.Increment(ref changes);
        _source.Configure([Primary() with { Password = "wrong" }]);
        _primary.AddYenc("a@x", [1]);

        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.Equal(1, changes);

        _primary.Password = "wrong";
        _time.Advance(TimeSpan.FromMinutes(5));
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        Assert.Equal(2, changes);
    }

    // ---- Quota ----

    [Fact]
    public async Task A_used_up_quota_skips_the_server_until_the_counter_is_reset()
    {
        var changes = 0;
        _source.StateChanged += () => Interlocked.Increment(ref changes);
        _source.Configure([Primary() with { QuotaBytes = 150_000, QuotaUsedBytes = 0 }, Backup()]);
        for (var i = 0; i < 3; i++)
        {
            _primary.AddYenc($"q{i}@x", YencArticles.Random(100_000, seed: i));
            _backup.AddYenc($"q{i}@x", YencArticles.Random(100_000, seed: i));
        }

        await _source.FetchAsync("q0@x", [], Start, CancellationToken.None);
        Assert.False(_source.Status().Servers[0].QuotaExhausted);
        await _source.FetchAsync("q1@x", [], Start, CancellationToken.None);
        var status = _source.Status().Servers[0];
        Assert.True(status.QuotaExhausted);
        Assert.True(status.QuotaUsedBytes > 200_000);
        Assert.Equal(1, changes);

        await _source.FetchAsync("q2@x", [], Start, CancellationToken.None);
        Assert.Equal(2, _primary.BodyRequests);
        Assert.Equal(1, _backup.BodyRequests);

        _source.ResetQuota(1);
        Assert.Equal(0, _source.QuotaUsed(1));
        Assert.Equal(2, changes);
        await _source.FetchAsync("q2@x", [], Start, CancellationToken.None);
        Assert.Equal(3, _primary.BodyRequests);
    }

    [Fact]
    public async Task A_used_up_quota_on_the_only_server_is_a_server_error()
    {
        _source.Configure([Primary() with { QuotaBytes = 1_000_000_000, QuotaUsedBytes = 1_000_000_000 }]);
        _primary.AddYenc("a@x", [1]);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));

        Assert.Contains("quota of 1 GB is used up", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _primary.ConnectionsOpened);
    }

    [Fact]
    public async Task A_required_server_with_a_used_up_quota_pauses()
    {
        _source.Configure([Primary() with { QuotaBytes = 10, QuotaUsedBytes = 10, OnFailure = ServerFailureAction.PauseDownloads }, Backup()]);
        _backup.AddYenc("a@x", [1]);

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], Start, CancellationToken.None));

        Assert.Contains("quota", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _backup.BodyRequests);
    }

    [Fact]
    public async Task Quota_bytes_are_handed_over_for_storing_once()
    {
        var saved = new List<IReadOnlyDictionary<int, long>>();
        _source.SaveQuotaUsage = bytes => saved.Add(bytes);
        _source.Configure([Primary() with { QuotaBytes = 1_000_000_000, QuotaUsedBytes = 500 }, Backup()]);
        _primary.AddYenc("a@x", YencArticles.Random(10_000));

        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);
        _source.FlushQuotaUsage();
        _source.FlushQuotaUsage();

        var stored = Assert.Single(saved);
        Assert.Equal([1], stored.Keys);
        Assert.Equal(_source.QuotaUsed(1) - 500, stored[1]);

        // Reconfiguring (e.g. after a save) keeps the live counter; removing the quota drops it.
        _source.Configure([Primary() with { QuotaBytes = 2_000_000_000, QuotaUsedBytes = 500 }, Backup()]);
        Assert.True(_source.QuotaUsed(1) > 10_000);
        _source.Configure([Primary(), Backup()]);
        Assert.Null(_source.QuotaUsed(1));
        Assert.Null(_source.Status().Servers[0].QuotaUsedBytes);
    }

    [Fact]
    public async Task Failed_storing_keeps_the_bytes_for_the_next_flush()
    {
        var calls = 0;
        long stored = 0;
        _source.SaveQuotaUsage = bytes =>
        {
            if (++calls == 1)
            {
                throw new IOException("disk full");
            }

            stored += bytes[1];
        };
        _source.Configure([Primary() with { QuotaBytes = 1_000_000_000 }]);
        _primary.AddYenc("a@x", YencArticles.Random(1000));
        await _source.FetchAsync("a@x", [], Start, CancellationToken.None);

        _source.FlushQuotaUsage();
        _source.FlushQuotaUsage();

        Assert.Equal(_source.QuotaUsed(1), stored);
    }

    // ---- Timeout, certificate check, tries ----

    [Fact]
    public async Task The_server_timeout_is_the_read_timeout()
    {
        await using var source = new UsenetArticleSource(_time, NullLogger<UsenetArticleSource>.Instance, new UsenetPoolOptions());
        source.Configure([Primary() with { TimeoutSeconds = 1 }]);
        _primary.AddYenc("a@x", [1]);
        _primary.StallBodies = true;

        var error = await Assert.ThrowsAsync<UsenetServerException>(() => source.FetchAsync("a@x", [], Start, CancellationToken.None));

        Assert.Contains("within 1 seconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_connect_timeout_is_never_longer_than_the_server_timeout()
    {
        var options = new UsenetPoolOptions();

        Assert.Equal(TimeSpan.FromSeconds(15), options.SettingsFor(Primary()).ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SettingsFor(Primary()).ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.SettingsFor(Primary() with { TimeoutSeconds = 10 }).ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.SettingsFor(Primary() with { TimeoutSeconds = 120 }).ReadTimeout);
    }

    [Theory]
    [InlineData(CertificateCheck.Strict, false)]
    [InlineData(CertificateCheck.Minimal, true)]
    [InlineData(CertificateCheck.Off, true)]
    public async Task Self_signed_certificate_depends_on_the_certificate_check(CertificateCheck check, bool works)
    {
        using var certificate = FakeNntpServer.CreateCertificate();
        await using var server = new FakeNntpServer(certificate);
        await using var source = new UsenetArticleSource(
            _time, NullLogger<UsenetArticleSource>.Instance, new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(5) });
        source.Configure([new UsenetServer(9, "TLS", "localhost", server.Port, true, string.Empty, string.Empty, 2, 0, true, 0, CertificateCheck: check)]);
        server.AddYenc("tls@x", [1, 2]);

        if (works)
        {
            Assert.Equal([1, 2], (await source.FetchAsync("tls@x", [], Start, CancellationToken.None)).Data);
        }
        else
        {
            var error = await Assert.ThrowsAsync<UsenetServerException>(() => source.FetchAsync("tls@x", [], Start, CancellationToken.None));
            Assert.Contains("SSL", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Minimal_still_wants_a_certificate_for_the_host_name()
    {
        using var certificate = FakeNntpServer.CreateCertificate();
        var minimal = NntpConnectionSettings.ValidationFor(CertificateCheck.Minimal)!;
        var off = NntpConnectionSettings.ValidationFor(CertificateCheck.Off)!;

        Assert.Null(NntpConnectionSettings.ValidationFor(CertificateCheck.Strict));
        Assert.True(minimal(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(minimal(this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(minimal(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.True(off(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public async Task Tries_per_article_come_from_the_setting()
    {
        var tries = 1;
        await using var source = new UsenetArticleSource(
            _time,
            NullLogger<UsenetArticleSource>.Instance,
            new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10), AttemptsSetting = () => tries });
        source.Configure([Primary()]);
        _primary.AddYenc("a@x", [1]);

        _primary.DropNextBodies(1);
        await Assert.ThrowsAsync<UsenetServerException>(() => source.FetchAsync("a@x", [], Start, CancellationToken.None));
        Assert.Equal(1, _primary.BodyRequests);

        tries = 5;
        _primary.DropNextBodies(4);
        Assert.Equal([1], (await source.FetchAsync("a@x", [], Start, CancellationToken.None)).Data);
        Assert.Equal(6, _primary.BodyRequests);
    }

    private UsenetArticleSource NewSource() => new(
        _time,
        NullLogger<UsenetArticleSource>.Instance,
        new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10), ConnectTimeout = TimeSpan.FromSeconds(5) });

    private UsenetServer Primary() => new(1, "Primary", "127.0.0.1", _primary.Port, false, "user", "pass", 4, 0, true, 0);

    private UsenetServer Backup() => new(2, "Backup", "127.0.0.1", _backup.Port, false, "block", "pass2", 2, 1, true, 0);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}
