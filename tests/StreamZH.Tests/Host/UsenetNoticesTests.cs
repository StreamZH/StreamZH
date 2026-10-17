// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Host.Status;
using StreamZH.Library.Usenet;
using StreamZH.Tests.Library;
using StreamZH.Tests.Usenet;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Host;

public sealed class UsenetNoticesTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeNntpServer _server = new() { Username = "user", Password = "pass" };
    private readonly UsenetArticleSource _source;
    private readonly UsenetServerService _service;

    public UsenetNoticesTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _source = new UsenetArticleSource(_time, NullLogger<UsenetArticleSource>.Instance, new UsenetPoolOptions { ReadTimeout = TimeSpan.FromSeconds(10) });
        _service = new UsenetServerService(new UsenetServerRepository(database), _source, _time, NullLogger<UsenetServerService>.Instance);
    }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        await _server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Healthy_servers_have_no_notices()
    {
        _service.Create(Local("Fine"));

        Assert.Empty(Notices());
        Assert.Empty(Notices(withService: false));
    }

    [Fact]
    public async Task A_skipped_server_shows_until_it_works_again()
    {
        var server = _service.Create(Local("Eweka") with { Password = "wrong" });
        _server.AddYenc("a@x", [1]);
        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None));

        var notice = Assert.Single(Notices());
        Assert.Equal($"usenet-server-{server.Id}", notice.Key);
        Assert.Equal(NoticeLevels.Warn, notice.Level);
        var until = TimeZoneInfo.ConvertTime(_time.GetUtcNow().AddMinutes(5), _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal($"Eweka skipped until {until}", notice.Title);
        Assert.Contains("481", notice.Sub, StringComparison.Ordinal);
        Assert.EndsWith("retry in 5 min", notice.Sub, StringComparison.Ordinal);
        Assert.Equal("/settings/usenet", notice.Link);
        Assert.False(notice.Dismissible);

        _server.Password = "wrong";
        _time.Advance(TimeSpan.FromMinutes(5));
        await _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Empty(Notices());
    }

    [Fact]
    public async Task A_required_server_is_an_error_that_says_downloads_are_paused()
    {
        _service.Create(Local("Eweka") with { Password = "wrong", OnFailure = ServerFailureAction.PauseDownloads });
        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None));

        var notice = Assert.Single(Notices());
        Assert.Equal(NoticeLevels.Error, notice.Level);
        Assert.Equal("Eweka unavailable, downloads paused", notice.Title);
    }

    [Fact]
    public async Task An_optional_server_does_not_show_its_errors()
    {
        _service.Create(Local("Flaky") with { Password = "wrong", OnFailure = ServerFailureAction.IgnoreLonger });
        await Assert.ThrowsAsync<UsenetServerException>(() => _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None));

        Assert.False(_service.Status().Servers.Single().Healthy);
        Assert.Empty(Notices());
    }

    [Fact]
    public async Task A_used_up_quota_shows_until_the_counter_is_reset()
    {
        var server = _service.Create(Local("Block") with { QuotaBytes = 1000 });
        _server.AddYenc("a@x", YencArticles.Random(5000));
        await _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None);

        var notice = Assert.Single(Notices());
        Assert.Equal($"usenet-quota-{server.Id}", notice.Key);
        Assert.Equal(NoticeLevels.Warn, notice.Level);
        Assert.Equal("Block: quota of 0 GB used up", notice.Title);

        _service.ResetQuota(server.Id);
        Assert.Empty(Notices());
    }

    [Theory]
    [InlineData(6, null, null, false)]
    [InlineData(5, "warn", "expires", true)]
    [InlineData(1, "warn", "expires", true)]
    [InlineData(0, "warn", "expires", true)]
    [InlineData(-1, "error", "expired", false)]
    [InlineData(-5, "error", "expired", false)]
    [InlineData(-6, null, null, false)]
    public void Account_expiry_shows_five_days_before_and_after(int days, string? level, string? verb, bool dismissible)
    {
        var expiry = Today.AddDays(days);
        var server = _service.Create(Local("Newshosting") with { ExpiresOn = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

        var notices = Notices();

        if (level is null)
        {
            Assert.Empty(notices);
            return;
        }

        var notice = Assert.Single(notices);
        Assert.Equal($"usenet-expiry-{server.Id}-{server.ExpiresOn}", notice.Key);
        Assert.Equal(level, notice.Level);
        Assert.Equal($"Newshosting account {verb} on {expiry.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}", notice.Title);
        Assert.Equal(dismissible, notice.Dismissible);
    }

    [Fact]
    public void Disabled_servers_have_no_notices()
    {
        _service.Create(Local("Old") with { Enabled = false, ExpiresOn = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

        Assert.Empty(Notices());
    }

    private List<OngoingNotice> Notices(bool withService = true)
    {
        var notices = new List<OngoingNotice>();
        UsenetNotices.Add(notices, withService ? _service : null, _time);
        return notices;
    }

    private UsenetServer Local(string name) => new(0, name, "127.0.0.1", _server.Port, false, "user", "pass", 2, 0, true, 0);
}
