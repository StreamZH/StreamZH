// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Host.Auth;
using StreamZH.Host.Users;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Users;

/// <summary>A migrated database in a temporary folder with the user management services on top.</summary>
public sealed class UserTestBed : IDisposable
{
    public UserTestBed()
    {
        Directory.CreateDirectory(Directory_);
        Database = new Database(Path.Combine(Directory_, "streamzh.db"));
        Database.Migrate();
        Users = new UserRepository(Database);
        Passwords = new PasswordService(Users);
        Requests = new RequestRepository(Database);
        Links = new DiscordLinkRepository(Database);
        Invites = new InviteRepository(Database);
        Codes = new DiscordLinkCodes(Time);
        Sessions = new UserSessionCache(Users, Time);
        Service = new UserService(Users, Passwords, Requests, Links, Codes, Sessions, new BasicCredentialCache(), Time, NullLogger<UserService>.Instance);
        InviteService = new InviteService(Invites, Service, Time, NullLogger<InviteService>.Instance);
    }

    public string Directory_ { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-users-{Guid.NewGuid():N}");

    public ManualTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public Database Database { get; }

    public UserRepository Users { get; }

    public PasswordService Passwords { get; }

    public RequestRepository Requests { get; }

    public DiscordLinkRepository Links { get; }

    public InviteRepository Invites { get; }

    public DiscordLinkCodes Codes { get; }

    public UserSessionCache Sessions { get; }

    public UserService Service { get; }

    public InviteService InviteService { get; }

    public static UserActor Owner(int id) => new(id, UserRole.Owner);

    public int Add(string username, UserRole role) => Users.Create(username, Passwords.Hash("long enough password"), role);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Directory_, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class UserRepositoryTests : IDisposable
{
    private readonly UserTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void Accounts_round_trip_with_profile_state_and_limits()
    {
        var id = _bed.Users.Create(
            new UserAccount
            {
                Username = "janedoe", Role = UserRole.Requester, DisplayName = "Jane Doe", Email = "jane@example.com", AutoApprove = true,
                MovieRequestLimit = 5, SeriesRequestLimit = null, RequestLimitDays = 7,
            },
            _bed.Passwords.Hash("long enough password"));

        var account = _bed.Users.FindAccount(id)!;
        Assert.Equal("Jane Doe", account.Name);
        Assert.Equal(UserRole.Requester, account.Role);
        Assert.True(account.Enabled);
        Assert.True(account.AutoApprove);
        Assert.Equal(5, account.MovieRequestLimit);
        Assert.Null(account.SeriesRequestLimit);
        Assert.True(account.HasCustomLimits);
        Assert.Null(account.LastLogin);
        Assert.Equal(DateTimeKind.Utc, account.CreatedAt.Kind);
        Assert.Equal(id, _bed.Users.FindAccountByUsername("JANEDOE")!.Id);

        _bed.Users.Update(account with { Role = UserRole.Friend, Enabled = false, Email = null, AutoApprove = null, RequestLimitDays = null, MovieRequestLimit = null });
        var changed = _bed.Users.FindAccount(id)!;
        Assert.Equal(UserRole.Friend, changed.Role);
        Assert.False(changed.Enabled);
        Assert.Null(changed.Email);
        Assert.Null(changed.AutoApprove);
        Assert.False(changed.HasCustomLimits);

        // The sign-in record carries state and session version.
        var user = _bed.Users.FindById(id)!;
        Assert.False(user.Enabled);
        Assert.Equal(0, user.SessionVersion);
        _bed.Users.BumpSessionVersion(id);
        _bed.Users.ChangePassword(id, _bed.Passwords.Hash("another password"));
        Assert.Equal(2, _bed.Users.FindById(id)!.SessionVersion);

        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        _bed.Users.SetLastLogin(id, when);
        Assert.Equal(when, _bed.Users.FindAccount(id)!.LastLogin);

        _bed.Users.UpdateProfile(id, "J. Doe", "j@example.com");
        Assert.Equal("J. Doe", _bed.Users.FindAccount(id)!.DisplayName);

        _bed.Users.Delete(id);
        Assert.Null(_bed.Users.FindAccount(id));
    }

    [Fact]
    public void Counts_enabled_owners_and_requests_per_user()
    {
        var owner = _bed.Add("owner1", UserRole.Owner);
        var second = _bed.Add("owner2", UserRole.Owner);
        var friend = _bed.Add("friend1", UserRole.Friend);
        Assert.Equal(2, _bed.Users.CountEnabledOwners());
        _bed.Users.Update(_bed.Users.FindAccount(second)! with { Enabled = false });
        Assert.Equal(1, _bed.Users.CountEnabledOwners());

        var now = DateTime.UtcNow;
        _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 1, Title = "A", RequestedBy = friend, Created = now, Updated = now });
        _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 2, Title = "B", RequestedBy = friend, Created = now, Updated = now });
        _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 3, Title = "C", RequestedBy = null, Created = now, Updated = now });
        var counts = _bed.Users.RequestCounts();
        Assert.Equal(2, counts[friend]);
        Assert.False(counts.ContainsKey(owner));

        // Ordered by role, then name.
        Assert.Equal(["owner1", "owner2", "friend1"], _bed.Users.All().Select(u => u.Username));
    }

    [Fact]
    public void A_disabled_account_cannot_sign_in()
    {
        var id = _bed.Add("janedoe", UserRole.Friend);
        Assert.NotNull(_bed.Passwords.Verify("janedoe", "long enough password"));
        _bed.Users.Update(_bed.Users.FindAccount(id)! with { Enabled = false });
        Assert.Null(_bed.Passwords.Verify("janedoe", "long enough password"));
    }
}

public sealed class UserRequestPolicyTests
{
    private sealed class Defaults(bool friends, bool requesters, int? movies, int? series, int days) : IRequestDefaults
    {
        public bool AutoApprove(UserRole role) => role switch
        {
            UserRole.Owner or UserRole.Admin => true,
            UserRole.Friend => friends,
            _ => requesters,
        };

        public int? MovieLimit => movies;

        public int? SeriesLimit => series;

        public int LimitDays => days;
    }

    [Fact]
    public void Role_defaults_apply_unless_the_user_has_own_values()
    {
        var builtIn = new DefaultRequestDefaults();
        Assert.True(UserRequestPolicy.For(new UserAccount { Role = UserRole.Owner }, builtIn).AutoApprove);
        Assert.True(UserRequestPolicy.For(new UserAccount { Role = UserRole.Admin }, builtIn).AutoApprove);
        Assert.True(UserRequestPolicy.For(new UserAccount { Role = UserRole.Friend }, builtIn).AutoApprove);
        Assert.False(UserRequestPolicy.For(new UserAccount { Role = UserRole.Requester }, builtIn).AutoApprove);
        Assert.Equal(7, UserRequestPolicy.For(new UserAccount { Role = UserRole.Requester }, builtIn).LimitDays);

        var strict = new Defaults(false, false, 3, 1, 14);
        var friend = UserRequestPolicy.For(new UserAccount { Role = UserRole.Friend }, strict);
        Assert.Equal(new RequestPolicy(false, 3, 1, 14), friend);
        Assert.Equal(3, friend.LimitFor(MediaType.Movie));
        Assert.Equal(1, friend.LimitFor(MediaType.Series));

        // Admins have no limits by default, but follow custom ones.
        Assert.Equal(new RequestPolicy(true, null, null, 14), UserRequestPolicy.For(new UserAccount { Role = UserRole.Admin }, strict));
        Assert.Equal(
            new RequestPolicy(true, 10, null, 30),
            UserRequestPolicy.For(new UserAccount { Role = UserRole.Requester, AutoApprove = true, MovieRequestLimit = 10, RequestLimitDays = 30 }, strict));
        Assert.False(UserRequestPolicy.For(new UserAccount { Role = UserRole.Owner, AutoApprove = false }, strict).AutoApprove);
    }
}

public sealed class DiscordLinkCodeTests
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public void Codes_are_short_unambiguous_single_use_and_expire()
    {
        var codes = new DiscordLinkCodes(_time);
        var code = codes.Create(7);
        Assert.Equal(DiscordLinkCodes.Length, code.Code.Length);
        Assert.All(code.Code, c => Assert.Contains(c, DiscordLinkCodes.Alphabet));
        Assert.DoesNotContain(code.Code, c => c is '0' or 'O' or '1' or 'I' or 'L');
        Assert.Equal(_time.Now + DiscordLinkCodes.Lifetime, code.Expires);
        Assert.Equal(code, codes.Active(7));

        // Case and spaces do not matter; a code works once.
        Assert.True(codes.TryRedeem($" {code.Code.ToLowerInvariant()[..3]} {code.Code[3..]} ", out var userId));
        Assert.Equal(7, userId);
        Assert.False(codes.TryRedeem(code.Code, out _));
        Assert.Null(codes.Active(7));

        var expiring = codes.Create(8);
        _time.Advance(DiscordLinkCodes.Lifetime);
        Assert.False(codes.TryRedeem(expiring.Code, out _));
        Assert.False(codes.TryRedeem("", out _));
        Assert.False(codes.TryRedeem("TOOLONGCODE", out _));
    }

    [Fact]
    public void A_new_code_replaces_the_users_old_one()
    {
        var codes = new DiscordLinkCodes(_time);
        var first = codes.Create(7);
        var second = codes.Create(7);
        var other = codes.Create(9);
        Assert.False(codes.TryRedeem(first.Code, out _));
        Assert.True(codes.TryRedeem(second.Code, out var id));
        Assert.Equal(7, id);

        codes.Cancel(9);
        Assert.False(codes.TryRedeem(other.Code, out _));
    }
}

public sealed class UserSessionCacheTests : IDisposable
{
    private readonly UserTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void Changes_behind_streamzhs_back_apply_after_the_cache_window_and_at_once_after_forget()
    {
        var id = _bed.Add("janedoe", UserRole.Friend);
        var principal = UserClaims.Principal(_bed.Users.FindById(id)!, AuthSchemes.Cookie);
        Assert.Equal(SessionCheck.Valid, _bed.Sessions.Check(principal, out _));

        // Disabled directly in the database: the cached state is used for up to 30 s, then the session is rejected.
        _bed.Users.Update(_bed.Users.FindAccount(id)! with { Enabled = false });
        _bed.Time.Advance(UserSessionCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(SessionCheck.Valid, _bed.Sessions.Check(principal, out _));
        _bed.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SessionCheck.Rejected, _bed.Sessions.Check(principal, out _));

        // Enabled again, then the sessions are ended: Forget makes it apply immediately.
        _bed.Users.Update(_bed.Users.FindAccount(id)! with { Enabled = true });
        _bed.Sessions.Forget(id);
        Assert.Equal(SessionCheck.Valid, _bed.Sessions.Check(principal, out _));
        _bed.Users.BumpSessionVersion(id);
        _bed.Sessions.Forget(id);
        Assert.Equal(SessionCheck.Rejected, _bed.Sessions.Check(principal, out _));
    }

    [Fact]
    public void Role_change_without_new_version_refreshes_and_deleted_users_are_rejected()
    {
        var id = _bed.Add("janedoe", UserRole.Friend);
        var principal = UserClaims.Principal(_bed.Users.FindById(id)!, AuthSchemes.Cookie);
        _bed.Users.Update(_bed.Users.FindAccount(id)! with { Role = UserRole.Requester });
        _bed.Sessions.Forget(id);
        Assert.Equal(SessionCheck.Refresh, _bed.Sessions.Check(principal, out var current));
        Assert.Equal(UserRole.Requester, current!.Role);

        _bed.Users.Delete(id);
        _bed.Sessions.Forget(id);
        Assert.Equal(SessionCheck.Rejected, _bed.Sessions.Check(principal, out _));

        // Cookies from before 0.10 have no version claim: version 0, still valid.
        var other = _bed.Add("johnsmith", UserRole.Friend);
        var old = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new(System.Security.Claims.ClaimTypes.NameIdentifier, other.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(System.Security.Claims.ClaimTypes.Name, "johnsmith"),
                new(System.Security.Claims.ClaimTypes.Role, "Friend"),
            ],
            AuthSchemes.Cookie));
        Assert.Equal(SessionCheck.Valid, _bed.Sessions.Check(old, out _));
    }
}

public sealed class UserServiceTests : IDisposable
{
    private readonly UserTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static UserInput New(string username, string role) => new(username, "long enough password", role);

    [Fact]
    public void The_last_enabled_owner_cannot_be_lowered_disabled_or_deleted()
    {
        var owner = _bed.Add("owner1", UserRole.Owner);
        var api = new UserActor(0, UserRole.Owner);

        var lower = _bed.Service.Update(api, owner, new UserInput(Role: "admin"));
        Assert.Equal(UserError.Invalid, lower.Error);
        Assert.Equal(UserService.LastOwnerMessage, lower.Errors!["role"][0]);
        Assert.Equal(UserError.Invalid, _bed.Service.Update(api, owner, new UserInput(Enabled: false)).Error);
        Assert.Equal(UserError.Conflict, _bed.Service.Delete(api, owner).Error);

        // With a second owner it works.
        var second = _bed.Service.Create(UserTestBed.Owner(owner), New("owner2", "owner"));
        Assert.True(second.Ok);
        Assert.True(_bed.Service.Update(api, owner, new UserInput(Role: "admin")).Ok);
        Assert.Equal(UserError.Invalid, _bed.Service.Update(api, second.Value!.Id, new UserInput(Enabled: false)).Error);
    }

    [Fact]
    public void Admins_cannot_create_raise_or_touch_owners()
    {
        var owner = _bed.Add("owner1", UserRole.Owner);
        var adminId = _bed.Add("admin1", UserRole.Admin);
        var friend = _bed.Add("friend1", UserRole.Friend);
        var admin = new UserActor(adminId, UserRole.Admin);

        Assert.Equal(UserError.Forbidden, _bed.Service.Create(admin, New("sneaky", "owner")).Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.Update(admin, friend, new UserInput(Role: "owner")).Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.Update(admin, owner, new UserInput(Role: "friend")).Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.Update(admin, owner, new UserInput(DisplayName: "Renamed")).Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.Delete(admin, owner).Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.ResetPassword(admin, owner, "long enough password").Error);
        Assert.Equal(UserError.Forbidden, _bed.Service.RevokeSessions(admin, owner).Error);
        Assert.Equal(UserRole.Owner, _bed.Users.FindAccount(owner)!.Role);
        Assert.Equal(UserRole.Friend, _bed.Users.FindAccount(friend)!.Role);

        // Admins manage everyone else, incl. other admins.
        Assert.True(_bed.Service.Update(admin, friend, new UserInput(Role: "admin")).Ok);
        Assert.True(_bed.Service.Create(admin, New("helper", "admin")).Ok);
    }

    [Fact]
    public void Nobody_changes_their_own_role_or_disables_or_deletes_themselves()
    {
        _bed.Add("owner1", UserRole.Owner);
        var adminId = _bed.Add("admin1", UserRole.Admin);
        var admin = new UserActor(adminId, UserRole.Admin);
        Assert.Equal("You cannot change your own role.", _bed.Service.Update(admin, adminId, new UserInput(Role: "friend")).Errors!["role"][0]);
        Assert.Equal(UserError.Invalid, _bed.Service.Update(admin, adminId, new UserInput(Enabled: false)).Error);
        Assert.Equal(UserError.Conflict, _bed.Service.Delete(admin, adminId).Error);
        Assert.True(_bed.Service.Update(admin, adminId, new UserInput(DisplayName: "Admin")).Ok);
    }

    [Fact]
    public void Disable_role_change_and_password_end_sessions_but_profile_changes_do_not()
    {
        _bed.Add("owner1", UserRole.Owner);
        var friend = _bed.Add("friend1", UserRole.Friend);
        var api = new UserActor(0, UserRole.Owner);
        int Version() => _bed.Users.FindById(friend)!.SessionVersion;

        _bed.Service.Update(api, friend, new UserInput(DisplayName: "Friend", Email: "friend@example.com"));
        Assert.Equal(0, Version());
        _bed.Service.Update(api, friend, new UserInput(Enabled: false));
        Assert.Equal(1, Version());
        _bed.Service.Update(api, friend, new UserInput(Enabled: true));
        Assert.Equal(1, Version());
        _bed.Service.Update(api, friend, new UserInput(Role: "requester"));
        Assert.Equal(2, Version());
        _bed.Service.ResetPassword(api, friend, "brand new password");
        Assert.Equal(3, Version());
        _bed.Service.RevokeSessions(api, friend);
        Assert.Equal(4, Version());
        Assert.NotNull(_bed.Passwords.Verify("friend1", "brand new password"));
    }

    [Fact]
    public void Input_is_validated()
    {
        _bed.Add("owner1", UserRole.Owner);
        var api = new UserActor(0, UserRole.Owner);

        var bad = _bed.Service.Create(api, new UserInput("a b", "short", "superuser", new string('x', 101), "not-an-email", null, null, -1, 5, 0));
        Assert.Equal(UserError.Invalid, bad.Error);
        Assert.Equal(
            ["displayName", "email", "movieRequestLimit", "password", "requestLimitDays", "role", "username"],
            bad.Errors!.Keys.Order(StringComparer.Ordinal));

        Assert.Equal("This username is taken.", _bed.Service.Create(api, New("OWNER1", "friend")).Errors!["username"][0]);
        Assert.Equal(UserError.Invalid, _bed.Service.Create(api, new UserInput("janedoe", new string('x', 300), "friend")).Error);

        var ok = _bed.Service.Create(api, new UserInput("janedoe", "long enough password", "requester", " Jane Doe ", " jane@example.com ", null, false, 3, null, 7));
        Assert.True(ok.Ok);
        Assert.Equal("Jane Doe", ok.Value!.DisplayName);
        Assert.Equal("jane@example.com", ok.Value.Email);
        Assert.False(ok.Value.AutoApprove);
        Assert.Equal((3, (int?)null, 7), (ok.Value.MovieRequestLimit!.Value, ok.Value.SeriesRequestLimit, ok.Value.RequestLimitDays!.Value));

        // Without a period the limits are the role's defaults.
        var reset = _bed.Service.Update(api, ok.Value.Id, new UserInput(MovieRequestLimit: 3));
        Assert.Null(reset.Value!.MovieRequestLimit);
        Assert.False(reset.Value.HasCustomLimits);
    }

    [Fact]
    public void Deleting_a_user_keeps_their_requests_and_drops_the_discord_link()
    {
        _bed.Add("owner1", UserRole.Owner);
        var friend = _bed.Add("friend1", UserRole.Friend);
        var now = DateTime.UtcNow;
        var request = _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 1, Title = "A", RequestedBy = friend, Created = now, Updated = now });
        _bed.Links.Set(friend, "100000000000000001", "friend");
        var code = _bed.Codes.Create(friend);

        Assert.True(_bed.Service.Delete(new UserActor(0, UserRole.Owner), friend).Ok);
        Assert.Null(_bed.Users.FindAccount(friend));
        Assert.Null(_bed.Requests.Find(request)!.RequestedBy);
        Assert.Null(_bed.Links.ForUser(friend));
        Assert.False(_bed.Codes.TryRedeem(code.Code, out _));
    }

    [Fact]
    public void Own_password_change_needs_the_current_password()
    {
        var friend = _bed.Add("friend1", UserRole.Friend);
        var wrong = _bed.Service.ChangeOwnPassword(friend, "wrong password", "brand new password");
        Assert.Equal("The current password is wrong.", wrong.Errors!["currentPassword"][0]);
        Assert.Equal(UserError.Invalid, _bed.Service.ChangeOwnPassword(friend, "long enough password", "short").Error);
        Assert.True(_bed.Service.ChangeOwnPassword(friend, "long enough password", "brand new password").Ok);
        Assert.Equal(1, _bed.Users.FindById(friend)!.SessionVersion);
    }

    [Fact]
    public void Invites_create_accounts_with_their_role_until_used_up_expired_or_revoked()
    {
        var owner = _bed.Add("owner1", UserRole.Owner);
        var actor = UserTestBed.Owner(owner);
        Assert.Equal(UserError.Invalid, _bed.InviteService.Create(actor, new InviteInput("admin", null, null, null)).Error);
        Assert.Equal(UserError.Invalid, _bed.InviteService.Create(actor, new InviteInput("friend", null, 0, 0)).Error);

        var created = _bed.InviteService.Create(actor, new InviteInput("requester", "for Jane", 7, 2)).Value!;
        Assert.Equal(InviteService.TokenLength, created.Token.Length);
        Assert.Equal(InviteService.Hash(created.Token), created.Invite.TokenHash);
        Assert.DoesNotContain(created.Token, created.Invite.TokenHash, StringComparison.Ordinal);
        Assert.Equal(InviteState.Valid, _bed.InviteService.Lookup(created.Token).State);
        Assert.Equal(InviteState.NotFound, _bed.InviteService.Lookup("x").State);

        // Bad input does not use the invite up.
        Assert.Equal(UserError.Invalid, _bed.InviteService.Accept(created.Token, "owner1", "long enough password").Error);
        Assert.Equal(0, _bed.Invites.Find(created.Invite.Id)!.Uses);

        var jane = _bed.InviteService.Accept(created.Token, "janedoe", "long enough password");
        Assert.Equal(UserRole.Requester, jane.Value!.Role);
        Assert.NotNull(_bed.Users.FindAccount(jane.Value.Id)!.LastLogin);
        Assert.True(_bed.InviteService.Accept(created.Token, "johnsmith", "long enough password").Ok);
        Assert.Equal(UserError.Gone, _bed.InviteService.Accept(created.Token, "third", "long enough password").Error);
        Assert.Equal(InviteState.UsedUp, _bed.InviteService.Lookup(created.Token).State);

        var expiring = _bed.InviteService.Create(actor, new InviteInput("friend", null, 1, null)).Value!;
        _bed.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(InviteState.Expired, _bed.InviteService.Lookup(expiring.Token).State);
        Assert.Equal(UserError.Gone, _bed.InviteService.Accept(expiring.Token, "late", "long enough password").Error);

        var revoked = _bed.InviteService.Create(actor, new InviteInput("friend", null, null, null)).Value!;
        Assert.True(_bed.InviteService.Revoke(revoked.Invite.Id));
        Assert.Equal(InviteState.Revoked, _bed.InviteService.Lookup(revoked.Token).State);
        Assert.Null(_bed.Users.FindAccountByUsername("late"));
    }
}
