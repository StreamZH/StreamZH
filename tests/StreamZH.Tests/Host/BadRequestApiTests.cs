// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using StreamZH.Host.Api;

namespace StreamZH.Tests.Host;

/// <summary>
/// 1.0 RC: request bodies and parameters that cannot be read are answered with 400 and a field error on every endpoint
/// (before: an empty 400, and a 500 with a stack trace while developing).
/// </summary>
public sealed class BadRequestApiTests : IDisposable
{
    private readonly StreamZhFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static StringContent Body(string json, string type = "application/json") => new(json, Encoding.UTF8, type);

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
    }

    [Fact]
    public async Task An_unknown_enum_value_is_a_field_error_with_the_allowed_values()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var errors = await ErrorsAsync(await owner.PostAsync("/api/v1/rootfolders", Body("""{"path":"/data/media","mediaType":"Banana"}""")));

        Assert.StartsWith("This value is not valid. Use one of: Movie, Series", errors.GetProperty("mediaType")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_wrong_types_and_missing_bodies_are_400()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        Assert.Equal("The request body is not valid JSON.", (await ErrorsAsync(await owner.PostAsync("/api/v1/rootfolders", Body("""{"path":""")))).GetProperty("")[0].GetString());
        Assert.Equal("This value is not valid.", (await ErrorsAsync(await owner.PostAsync("/api/v1/rootfolders", Body("""{"path":5}""")))).GetProperty("path")[0].GetString());
        Assert.Equal("Send the request body as JSON.", (await ErrorsAsync(await owner.PostAsync("/api/v1/rootfolders", Body(string.Empty)))).GetProperty("")[0].GetString());
    }

    [Fact]
    public async Task Query_parameters_that_cannot_be_read_name_the_parameter()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        var errors = await ErrorsAsync(await owner.GetAsync("/api/v1/download/history?page=abc"));

        Assert.Equal("This value is not valid.", errors.GetProperty("page")[0].GetString());
    }

    /// <summary>SV4 X-13: routing skips a JSON endpoint for another content type; that is a 415, not "no such route".</summary>
    [Fact]
    public async Task A_known_route_with_another_content_type_is_415_and_an_unknown_route_stays_404()
    {
        var owner = await _factory.CreateOwnerClientAsync();

        foreach (var response in new[]
        {
            await owner.PutAsync("/api/v1/setup/guide", Body("""{"dismissed":true}""", "text/plain")),
            await owner.PostAsync("/api/v1/rootfolders", Body("""{"path":"/data/media"}""", "application/x-www-form-urlencoded")),
            await owner.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/rootfolders")),
        })
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ApiFallback.JsonMessage, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync("/api/v1/no-such-route", Body("{}", "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync("/api/v1/no-such-route", Body("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/no-such-route")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync("/api/v1/no-such-route/5")).StatusCode);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("$", "")]
    [InlineData("$.mediaType", "mediaType")]
    [InlineData("$.Items[0].Kind", "items[0].Kind")]
    public void Json_paths_become_field_names(string? path, string field) => Assert.Equal(field, ApiExceptionHandler.FieldName(path));
}
