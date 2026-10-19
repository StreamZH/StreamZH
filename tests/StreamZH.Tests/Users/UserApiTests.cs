// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.General;
using StreamZH.Host.Users;
using StreamZH.Tests.Host;

namespace StreamZH.Tests.Users;

/// <summary>
/// The users, profile and invite API (0.10). Sign-in, setup and the invite endpoints share one rate limit (10 per
/// minute per address), so every test stays below it unless it tests the limit.
/// </summary>
public sealed class UserApiTests : IDisposable
{
    private const string Password = "long enough password";
    private readonly StreamZhFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<int> CreateUserAsync(HttpClient owner, string username, string role)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/users", new { username, password = Password, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> SignInAsync(string username, string password = Password)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Friends_and_requesters_reach_no_admin_endpoint_and_no_other_users_data()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var friendId = await CreateUserAsync(owner, "friend1", "friend");
        var requesterId = await CreateUserAsync(owner, "requester1", "requester");
        var invite = await (await owner.PostAsJsonAsync("/api/v1/invites", new { role = "friend" })).Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = invite.GetProperty("id").GetInt32();

        foreach (var (name, otherId) in new[] { ("friend1", requesterId), ("requester1", friendId) })
        {
            var client = await SignInAsync(name);
            var calls = new Func<Task<HttpResponseMessage>>[]
            {
                () => client.GetAsync("/api/v1/users"),
                () => client.GetAsync($"/api/v1/users/{otherId}"),
                () => client.PostAsJsonAsync("/api/v1/users", new { username = "sneaky", password = Password, role = "owner" }),
                () => client.PutAsJsonAsync($"/api/v1/users/{otherId}", new { role = "admin" }),
                () => client.DeleteAsync($"/api/v1/users/{otherId}"),
                () => client.PostAsJsonAsync($"/api/v1/users/{otherId}/password", new { password = "taken over password" }),
                () => client.PostAsync($"/api/v1/users/{otherId}/sessions/revoke", null),
                () => client.DeleteAsync($"/api/v1/users/{otherId}/discord"),
                () => client.GetAsync("/api/v1/invites"),
                () => client.PostAsJsonAsync("/api/v1/invites", new { role = "friend" }),
                () => client.DeleteAsync($"/api/v1/invites/{inviteId}"),
            };

            foreach (var call in calls)
            {
                var response = await call();
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {response.RequestMessage!.Method} {response.RequestMessage.RequestUri} gave {response.StatusCode}");
            }

            // Their own profile only: /me has no id parameter, and it shows the caller.
            var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
            Assert.Equal(name, me.GetProperty("username").GetString());
            Assert.False(me.GetProperty("permissions").GetProperty("isAdmin").GetBoolean());
            Assert.Equal(name == "friend1", me.GetProperty("permissions").GetProperty("canManageLibrary").GetBoolean());
        }

        // Nothing changed.
        var users = await owner.GetFromJsonAsync<JsonElement>("/api/v1/users");
        Assert.Equal(["friend1", "johndoe", "requester1"], users.EnumerateArray().Select(u => u.GetProperty("username").GetString()).Order());
        Assert.DoesNotContain(users.EnumerateArray(), u => u.GetProperty("role").GetString() == "owner" && u.GetProperty("username").GetString() != "johndoe");
    }

    [Fact]
    public async Task Owner_manages_accounts_and_changes_reach_running_sessions()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var created = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            username = "janedoe", password = Password, role = "requester", displayName = "Jane Doe", email = "jane@example.com",
            autoApprove = true, movieRequestLimit = 5, requestLimitDays = 7,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jane = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = jane.GetProperty("id").GetInt32();
        Assert.Equal("requester", jane.GetProperty("role").GetString());
        Assert.True(jane.GetProperty("effectiveAutoApprove").GetBoolean());
        Assert.Equal(5, jane.GetProperty("movieRequestLimit").GetInt32());
        Assert.Equal(JsonValueKind.Null, jane.GetProperty("seriesRequestLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, jane.GetProperty("discord").ValueKind);
        Assert.Equal(0, jane.GetProperty("requestCount").GetInt32());
        Assert.False(jane.TryGetProperty("passwordHash", out _));

        var bad = await owner.PostAsJsonAsync("/api/v1/users", new { username = "x", password = "short", role = "god", email = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "username", "password", "role", "email" })
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        // Requests count in the list; the usage is in the limit window.
        var now = DateTime.UtcNow;
        _factory.Services.GetRequiredService<RequestRepository>().Add(new MediaRequest
        {
            MediaType = MediaType.Movie, TmdbId = 603, Title = "Example Movie", RequestedBy = id, Created = now, Updated = now,
        });
        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/users")).EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        Assert.Equal(1, listed.GetProperty("requestCount").GetInt32());

        var session = await SignInAsync("janedoe");
        var me = await session.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(1, me.GetProperty("usage").GetProperty("movies").GetProperty("used").GetInt32());
        Assert.Equal(5, me.GetProperty("usage").GetProperty("movies").GetProperty("limit").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/users/{id}")).GetProperty("lastLogin").ValueKind);

        // A profile change keeps the session; disabling ends it at once and blocks sign-in with the usual message.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "requester", displayName = "Jane" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "requester", enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/me")).StatusCode);
        var disabled = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "janedoe", password = Password });
        var wrong = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "janedoe", password = "not the password" });
        Assert.Equal(wrong.StatusCode, disabled.StatusCode);
        string? Detail(JsonElement problem) => problem.GetProperty("detail").GetString();
        Assert.Equal(
            Detail(await wrong.Content.ReadFromJsonAsync<JsonElement>()),
            Detail(await disabled.Content.ReadFromJsonAsync<JsonElement>()));

        // Enabled and promoted to friend: the new role applies to the next request.
        await owner.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "friend", enabled = true });
        session = await SignInAsync("janedoe");
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/movies")).StatusCode);
        await owner.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "requester" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/movies")).StatusCode);

        // Reset password and revoke end sessions; the old password stops working.
        session = await SignInAsync("janedoe");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/v1/users/{id}/password", new { password = "a brand new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/me")).StatusCode);
        session = await SignInAsync("janedoe", "a brand new password");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/users/{id}/sessions/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/me")).StatusCode);

        // Delete keeps the request, without the user.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/users/{id}")).StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<RequestRepository>().ForTitle(MediaType.Movie, 603).Single().RequestedBy);
    }

    [Fact]
    public async Task The_owner_is_protected_from_admins_and_from_themselves()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var ownerId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetInt32();
        var friendId = await CreateUserAsync(owner, "friend1", "friend");
        await CreateUserAsync(owner, "admin1", "admin");
        var admin = await SignInAsync("admin1");

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/users", new { username = "boss", password = Password, role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/v1/users/{friendId}", new { role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/v1/users/{ownerId}", new { role = "friend" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/v1/users/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/users/{ownerId}/password", new { password = "taken over password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/v1/users/{ownerId}/sessions/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/users/{friendId}", new { role = "admin" })).StatusCode);

        // The last owner: no lowering, disabling or deleting, also not by themselves.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/users/{ownerId}", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/users/{ownerId}", new { role = "owner", enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/v1/users/{ownerId}")).StatusCode);
        Assert.Equal("owner", (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/users/{ownerId}")).GetProperty("role").GetString());

        // Signing out everywhere on yourself keeps this browser signed in.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/users/{ownerId}/sessions/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Profile_edits_password_change_and_discord_link_codes()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        await CreateUserAsync(owner, "janedoe", "requester");
        var jane = await SignInAsync("janedoe");
        var other = await SignInAsync("janedoe");

        var bad = await jane.PutAsJsonAsync("/api/v1/me", new { displayName = "Jane", email = "not an email" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var profile = await (await jane.PutAsJsonAsync("/api/v1/me", new { displayName = "Jane Doe", email = "jane@example.com" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Jane Doe", profile.GetProperty("displayName").GetString());
        Assert.Equal("requester", profile.GetProperty("role").GetString());

        // A requester cannot sneak a role or limits in through the profile.
        await jane.PutAsJsonAsync("/api/v1/me", new { displayName = "Jane Doe", role = "owner", autoApprove = true });
        var account = _factory.Services.GetRequiredService<UserRepository>().FindAccountByUsername("janedoe")!;
        Assert.Equal(UserRole.Requester, account.Role);
        Assert.Null(account.AutoApprove);

        var wrong = await jane.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = "not my password", newPassword = "my new password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await jane.PostAsJsonAsync("/api/v1/me/password", new { currentPassword = Password, newPassword = "my new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jane.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);

        var code = await (await jane.PostAsync("/api/v1/me/discord/link-code", null)).Content.ReadFromJsonAsync<JsonElement>();
        var text = code.GetProperty("code").GetString()!;
        Assert.Equal(6, text.Length);
        Assert.Equal(text, (await jane.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("linkCode").GetProperty("code").GetString());

        // The bot redeems the code once and stores the link; the profile shows it and can remove it.
        var codes = _factory.Services.GetRequiredService<IDiscordLinkCodes>();
        Assert.True(codes.TryRedeem(text, out var userId));
        Assert.False(codes.TryRedeem(text, out _));
        _factory.Services.GetRequiredService<DiscordLinkRepository>().Set(userId, "100000000000000001", "janedoe");
        var linked = await jane.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("100000000000000001", linked.GetProperty("discord").GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, linked.GetProperty("linkCode").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await jane.DeleteAsync("/api/v1/me/discord")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await jane.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("discord").ValueKind);
    }

    [Fact]
    public async Task Invites_sign_people_in_with_their_role_until_used_up_expired_or_revoked()
    {
        var owner = await _factory.CreateOwnerClientAsync();
        var created = await (await owner.PostAsJsonAsync("/api/v1/invites", new { role = "requester", note = "for Jane", expiresInDays = 7, maxUses = 1 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString()!;
        Assert.EndsWith($"/invite/{token}", created.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/invites", new { role = "admin" })).StatusCode);

        // The list never shows the token again.
        var list = await owner.GetStringAsync("/api/v1/invites");
        Assert.DoesNotContain(token, list, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"active\"", list, StringComparison.Ordinal);

        var anonymous = _factory.CreateClient();
        var check = await anonymous.GetFromJsonAsync<JsonElement>($"/api/v1/invites/{token}");
        Assert.True(check.GetProperty("valid").GetBoolean());
        Assert.Equal("requester", check.GetProperty("role").GetString());
        Assert.Equal("for Jane", check.GetProperty("note").GetString());

        var accepted = await anonymous.PostAsJsonAsync($"/api/v1/invites/{token}/accept", new { username = "janedoe", password = Password });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var me = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("janedoe", me.GetProperty("username").GetString());
        Assert.Equal("requester", me.GetProperty("role").GetString());

        // Used up: the page says so, accepting is refused.
        Assert.False((await anonymous.GetFromJsonAsync<JsonElement>($"/api/v1/invites/{token}")).GetProperty("valid").GetBoolean());
        Assert.Equal(HttpStatusCode.Gone, (await _factory.CreateClient().PostAsJsonAsync($"/api/v1/invites/{token}/accept", new { username = "johnsmith", password = Password })).StatusCode);

        // Expired (stored with a date in the past) and revoked.
        var expiredToken = new string('e', InviteService.TokenLength);
        _factory.Services.GetRequiredService<InviteRepository>().Add(InviteService.Hash(expiredToken), UserRole.Friend, null, 1, DateTime.UtcNow.AddMinutes(-1), 1);
        Assert.Equal(HttpStatusCode.Gone, (await _factory.CreateClient().PostAsJsonAsync($"/api/v1/invites/{expiredToken}/accept", new { username = "late", password = Password })).StatusCode);

        var revoked = await (await owner.PostAsJsonAsync("/api/v1/invites", new { role = "friend" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/invites/{revoked.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await _factory.CreateClient().PostAsJsonAsync($"/api/v1/invites/{revoked.GetProperty("token").GetString()}/accept", new { username = "late", password = Password })).StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<UserRepository>().FindByUsername("late"));
        Assert.Contains("\"state\":\"revoked\"", await owner.GetStringAsync("/api/v1/invites"), StringComparison.Ordinal);

        // With an application URL (reverse proxy) the link uses it.
        _factory.Services.GetRequiredService<SettingsCache>().Set(ConfigKeys.ApplicationUrl, "https://streamzh.example.com/media");
        var proxied = await (await owner.PostAsJsonAsync("/api/v1/invites", new { role = "friend" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"https://streamzh.example.com/media/invite/{proxied.GetProperty("token").GetString()}", proxied.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Invite_endpoints_are_rate_limited()
    {
        var client = _factory.CreateClient();
        var token = new string('x', InviteService.TokenLength);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await client.GetAsync($"/api/v1/invites/{token}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync($"/api/v1/invites/{token}/accept", new { username = "janedoe", password = Password })).StatusCode);
    }
}
