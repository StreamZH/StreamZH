// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Usenet;
using StreamZH.Tests.Library;
using StreamZH.Usenet.Pool;

namespace StreamZH.Tests.Usenet;

public sealed class UsenetServerServiceTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeNntpServer _server = new() { Username = "user", Password = "top secret" };
    private readonly RecordingLogger _log = new();
    private readonly UsenetArticleSource _source;
    private readonly UsenetServerService _service;

    public UsenetServerServiceTests()
    {
        var database = new Database(_file);
        database.Migrate();
        _source = new UsenetArticleSource(_time, NullLogger<UsenetArticleSource>.Instance);
        _service = new UsenetServerService(new UsenetServerRepository(database), _source, _time, _log);
    }

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
    public void Schema_has_the_defaults_of_a_new_server()
    {
        var schema = UsenetServerService.Schema();

        Assert.Equal(563, schema.Port);
        Assert.True(schema.UseSsl);
        Assert.Equal(20, schema.Connections);
        Assert.Equal(0, schema.Priority);
        Assert.True(schema.Enabled);
    }

    [Fact]
    public void Create_trims_stores_and_reloads_the_pool()
    {
        var created = _service.Create(Valid("  Eweka ") with { Host = " news.eweka.nl ", Username = " me ", Password = " pass with spaces " });

        Assert.True(created.Id > 0);
        Assert.Equal("Eweka", created.Name);
        Assert.Equal("news.eweka.nl", created.Host);
        Assert.Equal("me", created.Username);
        Assert.Equal(" pass with spaces ", created.Password);
        Assert.Equal(20, _source.MaxConnections);

        _service.Create(Valid("Block") with { Connections = 8, Priority = 1 });
        _service.Create(Valid("Off") with { Connections = 50, Enabled = false });
        Assert.Equal(28, _source.MaxConnections);
        Assert.Equal(["Eweka", "Off", "Block"], _service.All().Select(s => s.Name));
    }

    [Fact]
    public void Validation_reports_every_problem_with_the_contract_keys()
    {
        var error = Assert.Throws<ValidationFailedException>(() => _service.Create(new UsenetServer(
            0, " ", "nntps://news.example.com:563", 0, true, string.Empty, "pw", 0, 100, true, -1)));

        Assert.Equal(
            ["connections", "host", "name", "port", "priority", "retentionDays", "username"],
            error.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("news.example.com", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("news.example.com:563", false)]
    [InlineData("news example", false)]
    [InlineData("nntp://news.example.com", false)]
    public void Checks_the_host_name(string host, bool valid)
    {
        var create = () => _service.Create(Valid("A") with { Host = host });
        if (valid)
        {
            create();
        }
        else
        {
            Assert.Contains("host", Assert.Throws<ValidationFailedException>(create).Errors.Keys);
        }
    }

    [Fact]
    public void Names_are_unique_ignoring_case()
    {
        var first = _service.Create(Valid("Eweka"));

        Assert.Contains("name", Assert.Throws<ValidationFailedException>(() => _service.Create(Valid("EWEKA"))).Errors.Keys);
        _service.Update(first.Id, first with { Name = "eweka" });
    }

    [Fact]
    public void Masked_password_keeps_the_stored_one_and_is_refused_for_a_new_server()
    {
        var created = _service.Create(Valid("A") with { Password = "stored" });

        var updated = _service.Update(created.Id, created with { Password = UsenetServerService.MaskedPassword, Connections = 30 });
        Assert.Equal("stored", updated.Password);
        Assert.Equal(30, updated.Connections);

        Assert.Equal("new", _service.Update(created.Id, created with { Password = "new" }).Password);
        Assert.Contains("password", Assert.Throws<ValidationFailedException>(() =>
            _service.Create(Valid("B") with { Password = UsenetServerService.MaskedPassword })).Errors.Keys);
    }

    [Fact]
    public void Update_and_delete_of_an_unknown_server_is_not_found()
    {
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => _service.Update(42, Valid("A"))).Error);
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => _service.Delete(42)).Error);
    }

    [Fact]
    public void Delete_reloads_the_pool()
    {
        var created = _service.Create(Valid("A"));
        _service.Delete(created.Id);

        Assert.Empty(_service.All());
        Assert.Equal(0, _source.MaxConnections);
    }

    [Fact]
    public async Task Test_logs_in_with_the_stored_password_and_reports_latency()
    {
        var created = _service.Create(Local("Local") with { Password = "top secret" });

        var result = await _service.TestAsync(created with { Password = UsenetServerService.MaskedPassword }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.StartsWith("Connected without SSL, logged in.", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.LatencyMs);
        Assert.Contains("DATE", _server.Commands);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("top secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_with_a_wrong_password_says_what_to_check()
    {
        var result = await _service.TestAsync(Local("Local") with { Password = "nope" }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("481", result.Message, StringComparison.Ordinal);
        Assert.Contains("Check the user name and password.", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_needs_a_password_for_a_masked_value_without_a_stored_server()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _service.TestAsync(Local("Local") with { Password = UsenetServerService.MaskedPassword }, CancellationToken.None));

        Assert.Equal(["password"], error.Errors.Keys);
    }

    [Fact]
    public void Status_lists_disabled_servers_too()
    {
        _service.Create(Local("On"));
        _service.Create(Local("Off") with { Enabled = false, Priority = 2 });

        var status = _service.Status();

        Assert.Equal(["On", "Off"], status.Servers.Select(s => s.Name));
        Assert.All(status.Servers, s => Assert.True(s.Healthy));
    }

    [Fact]
    public void Expert_settings_are_stored_and_default_to_the_old_behaviour()
    {
        var plain = _service.Create(Valid("Plain"));
        Assert.Equal(60, plain.TimeoutSeconds);
        Assert.Equal(CertificateCheck.Strict, plain.CertificateCheck);
        Assert.Equal(1, plain.ArticlesPerRequest);
        Assert.Equal(ServerFailureAction.Skip, plain.OnFailure);
        Assert.Null(plain.ExpiresOn);
        Assert.Null(plain.QuotaBytes);
        Assert.Null(plain.QuotaSince);
        Assert.Equal(string.Empty, plain.Notes);

        var expert = _service.Create(Valid("Expert") with
        {
            TimeoutSeconds = 120,
            CertificateCheck = CertificateCheck.Minimal,
            ArticlesPerRequest = 2,
            OnFailure = ServerFailureAction.PauseDownloads,
            ExpiresOn = "2026-10-22T00:00:00Z",
            QuotaBytes = 500 * UsenetServer.BytesPerGb,
            Notes = "  Block account, bought 2026  ",
        });

        var stored = _service.Find(expert.Id)!;
        Assert.Equal(120, stored.TimeoutSeconds);
        Assert.Equal(CertificateCheck.Minimal, stored.CertificateCheck);
        Assert.Equal(2, stored.ArticlesPerRequest);
        Assert.Equal(ServerFailureAction.PauseDownloads, stored.OnFailure);
        Assert.Equal("2026-10-22", stored.ExpiresOn);
        Assert.Equal(new DateOnly(2026, 10, 22), stored.ExpiryDate);
        Assert.Equal(500 * UsenetServer.BytesPerGb, stored.QuotaBytes);
        Assert.Equal(0, stored.QuotaUsedBytes);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, stored.QuotaSince);
        Assert.Equal("Block account, bought 2026", stored.Notes);
        Assert.Equal(20 + (20 * 2), _source.MaxConnections);
    }

    [Fact]
    public void Expert_settings_are_validated_with_their_own_keys()
    {
        var error = Assert.Throws<ValidationFailedException>(() => _service.Create(Valid("A") with
        {
            TimeoutSeconds = 5,
            CertificateCheck = (CertificateCheck)7,
            ArticlesPerRequest = 21,
            OnFailure = (ServerFailureAction)9,
            ExpiresOn = "31.01.2027",
            QuotaBytes = 0,
            Notes = new string('x', 2001),
        }));

        Assert.Equal(
            ["articlesPerRequest", "certificateCheck", "expiresOn", "notes", "onFailure", "quotaGb", "timeoutSeconds"],
            error.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Quota_counter_starts_when_set_survives_changes_and_resets()
    {
        _server.AddYenc("a@x", YencArticles.Random(50_000));
        var created = _service.Create(Local("Block"));
        await _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Equal(0, _service.Find(created.Id)!.QuotaUsedBytes);

        // Set: counting starts now, earlier downloads do not count.
        _time.Advance(TimeSpan.FromHours(1));
        var withQuota = _service.Update(created.Id, created with { QuotaBytes = 100 * UsenetServer.BytesPerGb });
        Assert.Equal(0, withQuota.QuotaUsedBytes);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, withQuota.QuotaSince);

        await _source.FetchAsync("a@x", [], DateTimeOffset.MinValue, CancellationToken.None);
        var used = _service.Find(created.Id)!.QuotaUsedBytes;
        Assert.True(used > 50_000);

        // A different amount keeps the counter.
        _time.Advance(TimeSpan.FromHours(1));
        var raised = _service.Update(created.Id, withQuota with { QuotaBytes = 200 * UsenetServer.BytesPerGb });
        Assert.Equal(used, raised.QuotaUsedBytes);
        Assert.Equal(withQuota.QuotaSince, raised.QuotaSince);

        // Reset: zero, counting from now.
        var reset = _service.ResetQuota(created.Id);
        Assert.Equal(0, reset.QuotaUsedBytes);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, reset.QuotaSince);

        // Removed: no counter.
        var removed = _service.Update(created.Id, raised with { QuotaBytes = null });
        Assert.Null(removed.QuotaSince);
        Assert.Null(_source.QuotaUsed(created.Id));
        Assert.Equal(LibraryError.Invalid, Assert.Throws<LibraryException>(() => _service.ResetQuota(created.Id)).Error);
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => _service.ResetQuota(999)).Error);
    }

    [Fact]
    public void Stored_quota_usage_only_counts_for_servers_with_a_quota()
    {
        var repository = new UsenetServerRepository(new Database(_file));
        var withQuota = _service.Create(Valid("Quota") with { QuotaBytes = UsenetServer.BytesPerGb });
        var without = _service.Create(Valid("Free"));

        repository.AddQuotaUsage(new Dictionary<int, long> { [withQuota.Id] = 1000, [without.Id] = 1000 });
        repository.AddQuotaUsage(new Dictionary<int, long> { [withQuota.Id] = 500 });

        Assert.Equal(1500, repository.Find(withQuota.Id)!.QuotaUsedBytes);
        Assert.Equal(0, repository.Find(without.Id)!.QuotaUsedBytes);
    }

    private static UsenetServer Valid(string name) => new(0, name, "news.example.com", 563, true, "user", "password", 20, 0, true, 0);

    private UsenetServer Local(string name) => new(0, name, "127.0.0.1", _server.Port, false, "user", "top secret", 2, 0, true, 0);

    private sealed class RecordingLogger : ILogger<UsenetServerService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
