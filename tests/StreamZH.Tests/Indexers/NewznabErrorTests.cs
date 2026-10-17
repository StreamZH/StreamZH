// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using StreamZH.Core.Indexers;
using StreamZH.Indexers.Newznab;
using static StreamZH.Tests.Indexers.NewznabTestSupport;

namespace StreamZH.Tests.Indexers;

public class NewznabErrorTests
{
    private static readonly MovieSearchCriteria Dune = new() { Titles = ["Dune"], Year = 2021, TmdbId = 438631 };

    private static Task<IndexerException> SearchFails(Func<HttpRequestMessage, HttpResponseMessage> respond, IndexerDefinition? indexer = null) =>
        Assert.ThrowsAsync<IndexerException>(() => Client(new StubIndexer { Respond = respond }).SearchAsync(indexer ?? Indexer(), Dune, CancellationToken.None));

    [Theory]
    [InlineData(100, "Incorrect user credentials")]
    [InlineData(101, "Account suspended")]
    [InlineData(102, "Insufficient privileges/not authorized")]
    public async Task Codes_100_to_199_are_auth_errors(int code, string description)
    {
        var ex = await SearchFails(_ => Xml($"""<?xml version="1.0"?><error code="{code}" description="{description}"/>"""));

        Assert.Equal(code, ex.Code);
        Assert.True(ex.IsAuthError);
        Assert.Null(ex.RetryAfter);
        Assert.Contains(description, ex.Message, StringComparison.Ordinal);
        Assert.Contains("TestIndexer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Radarr_unauthorized_fixture_is_an_auth_error()
    {
        var ex = await SearchFails(_ => Xml(Fixture("unauthorized.xml")));

        Assert.Equal(100, ex.Code);
        Assert.True(ex.IsAuthError);
    }

    [Fact]
    public async Task Missing_parameter_without_api_key_means_the_indexer_needs_a_key()
    {
        var ex = await SearchFails(_ => Xml("""<error code="200" description="Missing parameter"/>"""), Indexer(apiKey: ""));

        Assert.True(ex.IsAuthError);
        Assert.Contains("requires an API key", ex.Message, StringComparison.Ordinal);

        // With a key it is an ordinary error.
        var withKey = await SearchFails(_ => Xml("""<error code="200" description="Missing parameter"/>"""));
        Assert.Equal(200, withKey.Code);
        Assert.False(withKey.IsAuthError);
    }

    [Theory]
    [InlineData(500, "Request limit reached")]
    [InlineData(501, "Download limit reached")]
    [InlineData(429, "Too many requests")]
    [InlineData(900, "Request limit reached")]
    public async Task Request_limits_carry_a_retry_after(int code, string description)
    {
        var ex = await SearchFails(_ => Xml($"""<error code="{code}" description="{description}"/>"""));

        Assert.Equal(code, ex.Code);
        Assert.False(ex.IsAuthError);
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_header_overrides_the_default_back_off()
    {
        var ex = await SearchFails(_ =>
        {
            var response = Xml("""<error code="500" description="Request limit reached"/>""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }

    [Fact]
    public async Task Http_429_and_503_use_retry_after()
    {
        var tooMany = await SearchFails(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        Assert.Equal(TimeSpan.FromSeconds(30), tooMany.RetryAfter);
        Assert.Null(tooMany.Code);

        var noHeader = await SearchFails(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.Equal(TimeSpan.FromHours(1), noHeader.RetryAfter);

        var time = new ManualTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubIndexer
        {
            Respond = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(time.Now.AddMinutes(10));
                return response;
            },
        };
        var unavailable = await Assert.ThrowsAsync<IndexerException>(() =>
            Client(handler, new NewznabCapabilitiesCache(time)).SearchAsync(Indexer(), Dune, CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(10), unavailable.RetryAfter);
        Assert.Contains("unavailable (HTTP 503)", unavailable.Message, StringComparison.Ordinal);

        var plain503 = await SearchFails(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(plain503.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Http_401_and_403_are_auth_errors(HttpStatusCode status)
    {
        var ex = await SearchFails(_ => new HttpResponseMessage(status) { Content = new StringContent("denied") });

        Assert.True(ex.IsAuthError);
        Assert.Contains($"HTTP {(int)status}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_error_with_newznab_error_body_reports_the_newznab_error()
    {
        var ex = await SearchFails(_ => Xml("""<error code="100" description="Incorrect user credentials"/>""", HttpStatusCode.BadRequest, "text/xml"));

        Assert.Equal(100, ex.Code);
    }

    [Fact]
    public async Task Html_answers_are_reported_as_blocked()
    {
        var ex = await SearchFails(_ => Xml("<!DOCTYPE html><html><body>Cloudflare</body></html>", mediaType: "text/html"));

        Assert.Contains("HTML page", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broken_xml_is_reported()
    {
        var ex = await SearchFails(_ => Xml("<rss><channel><item>"));

        Assert.Contains("invalid XML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_codes_keep_code_and_description()
    {
        var ex = await SearchFails(_ => Xml("""<error code="203" description="Function not available"/>"""));

        Assert.Equal(203, ex.Code);
        Assert.Null(ex.RetryAfter);
        Assert.Contains("Function not available", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_key_never_appears_in_messages()
    {
        var ex = await SearchFails(_ => Xml($"""<error code="100" description="Invalid API key {ApiKey} for https://x.example/api?apikey={ApiKey}"/>"""));

        Assert.DoesNotContain(ApiKey, ex.ToString(), StringComparison.Ordinal);
        Assert.Contains(NewznabUrls.Redacted, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://indexer.example/api?t=search&apikey=abc123&q=x", "https://indexer.example/api?t=search&apikey=(removed)&q=x")]
    [InlineData("https://indexer.example/getnzb/1.nzb&i=5&r=abc123", "https://indexer.example/getnzb/1.nzb&i=5&r=(removed)")]
    [InlineData("<link>https://x/getnzb/1.nzb&amp;i=5&amp;r=abc123</link>", "<link>https://x/getnzb/1.nzb&amp;i=5&amp;r=(removed)</link>")]
    [InlineData("https://x/api?API_KEY=abc123", "https://x/api?API_KEY=(removed)")]
    [InlineData("key is my-secret-key", "key is (removed)")]
    [InlineData("https://x/dl/my-secret-key/1.nzb", "https://x/dl/(removed)/1.nzb")]
    public void Redacts_api_keys(string input, string expected) =>
        Assert.Equal(expected, NewznabUrls.Redact(input, "my-secret-key"));

    [Fact]
    public void Redacts_escaped_api_keys() =>
        Assert.Equal("x=(removed)", NewznabUrls.Redact("x=" + Uri.EscapeDataString("a+b/c"), "a+b/c"));
}
