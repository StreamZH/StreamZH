// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Events;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Core.Users;
using StreamZH.Data;
using StreamZH.Library.Discord;
using StreamZH.Tests.Library;

namespace StreamZH.Tests.Discord;

/// <summary>Discord's REST API as a recorder: every call is remembered; errors can be injected per call kind.</summary>
public sealed class FakeDiscordApi : IDiscordApi
{
    public const string BotId = "100000000000000099";
    public const string ApplicationId = "100000000000000098";

    public ConcurrentQueue<(string InteractionId, DiscordInteractionResponse Response)> Responses { get; } = new();

    public ConcurrentQueue<DiscordMessage> Edits { get; } = new();

    public ConcurrentQueue<(string ChannelId, DiscordMessage Message)> Sent { get; } = new();

    public ConcurrentQueue<string> DirectMessagesOpened { get; } = new();

    public ConcurrentQueue<(string? GuildId, IReadOnlyList<DiscordCommandDefinition> Commands)> Registrations { get; } = new();

    public List<DiscordGuild> Guilds { get; } = [new("100000000000000010", "Movie Night")];

    public List<DiscordChannel> Channels { get; } =
    [
        new("100000000000000020", "requests", 0),
        new("100000000000000021", "general", 0),
        new("100000000000000022", "Voice", 2),
    ];

    public List<DiscordRole> Roles { get; } =
    [
        new("100000000000000010", "@everyone"),
        new("100000000000000030", "Friends"),
        new("100000000000000031", "Bot role", Managed: true),
    ];

    /// <summary>Thrown by every call that takes the bot token (e.g. a rejected token).</summary>
    public DiscordApiException? TokenError { get; set; }

    /// <summary>Thrown when sending to a channel (not a DM).</summary>
    public Func<string, DiscordApiException?> SendError { get; set; } = _ => null;

    public Func<string, DiscordApiException?> OpenDmError { get; set; } = _ => null;

    public ConcurrentQueue<string> TokensUsed { get; } = new();

    public Task<DiscordUser> GetCurrentUserAsync(string token, CancellationToken cancellationToken) =>
        WithToken(token, () => new DiscordUser(BotId, "streamzh-bot", "StreamZH"));

    public Task<DiscordApplication> GetApplicationAsync(string token, CancellationToken cancellationToken) =>
        WithToken(token, () => new DiscordApplication(ApplicationId, "StreamZH"));

    public Task<DiscordGatewayInfo> GetGatewayAsync(string token, CancellationToken cancellationToken) =>
        WithToken(token, () => new DiscordGatewayInfo("wss://gateway.invalid", 1000, TimeSpan.Zero));

    public Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(string token, CancellationToken cancellationToken) =>
        WithToken<IReadOnlyList<DiscordGuild>>(token, () => Guilds);

    public Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(string token, string guildId, CancellationToken cancellationToken) =>
        WithToken<IReadOnlyList<DiscordChannel>>(token, () => Channels);

    public Task<IReadOnlyList<DiscordRole>> GetRolesAsync(string token, string guildId, CancellationToken cancellationToken) =>
        WithToken<IReadOnlyList<DiscordRole>>(token, () => Roles);

    public Task RegisterCommandsAsync(string token, string applicationId, string? guildId, IReadOnlyList<DiscordCommandDefinition> commands, CancellationToken cancellationToken) =>
        WithToken(token, () =>
        {
            Registrations.Enqueue((guildId, commands));
            return true;
        });

    public Task RespondAsync(string interactionId, string interactionToken, DiscordInteractionResponse response, CancellationToken cancellationToken)
    {
        Responses.Enqueue((interactionId, response));
        return Task.CompletedTask;
    }

    public Task EditOriginalAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
    {
        Edits.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task FollowUpAsync(string applicationId, string interactionToken, DiscordMessage message, CancellationToken cancellationToken)
    {
        Edits.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<string> OpenDirectMessageAsync(string token, string userId, CancellationToken cancellationToken) =>
        WithToken(token, () =>
        {
            if (OpenDmError(userId) is { } error)
            {
                throw error;
            }

            DirectMessagesOpened.Enqueue(userId);
            return "dm-" + userId;
        });

    public Task SendMessageAsync(string token, string channelId, DiscordMessage message, CancellationToken cancellationToken) =>
        WithToken(token, () =>
        {
            if (!channelId.StartsWith("dm-", StringComparison.Ordinal) && SendError(channelId) is { } error)
            {
                throw error;
            }

            Sent.Enqueue((channelId, message));
            return true;
        });

    private Task<T> WithToken<T>(string token, Func<T> result)
    {
        TokensUsed.Enqueue(token);
        if (TokenError is { } error)
        {
            throw error;
        }

        return Task.FromResult(result());
    }
}

/// <summary>Discover and requests as the bot sees them: fixed results, a configurable outcome, every draft remembered.</summary>
public sealed class FakeRequestGateway : IRequestGateway
{
    public static readonly RequestTitleState Fresh = new(false, false, null, null);

    public List<RequestCandidate> Movies { get; } =
    [
        new(MediaType.Movie, 603, "Example Movie", 1999, "A hacker learns the truth.", "/poster603.jpg", 8.2, Fresh),
        new(MediaType.Movie, 604, "Example Movie *Reloaded*", 2003, null, null, 7.0, new RequestTitleState(true, true, null, null)),
    ];

    public List<RequestCandidate> Series { get; } =
    [
        new(MediaType.Series, 1399, "Example Show", 2011, "Families fight for a throne.", "/poster1399.jpg", 8.4, Fresh),
    ];

    public List<RequestSeasonInfo> Seasons { get; } =
    [
        new(0, "Specials", 3, false, null),
        new(1, "Season 1", 10, true, null),
        new(2, "Season 2", 10, false, RequestStatus.Pending),
        new(3, "Season 3", 10, false, null),
        new(4, null, 8, false, null),
    ];

    public ConcurrentQueue<DiscordRequestDraft> Drafts { get; } = new();

    public Func<DiscordRequestDraft, RequestOutcome>? Outcome { get; set; }

    public Exception? SearchError { get; set; }

    public Task<IReadOnlyList<RequestCandidate>> SearchAsync(MediaType mediaType, string query, CancellationToken cancellationToken)
    {
        if (SearchError is { } error)
        {
            throw error;
        }

        IReadOnlyList<RequestCandidate> list = query == "nothing" ? [] : mediaType == MediaType.Movie ? Movies : Series;
        return Task.FromResult(list);
    }

    public Task<RequestCandidateDetails?> DetailsAsync(MediaType mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        var candidate = (mediaType == MediaType.Movie ? Movies : Series).FirstOrDefault(c => c.TmdbId == tmdbId);
        return Task.FromResult(candidate is null ? null : new RequestCandidateDetails(candidate, mediaType == MediaType.Series ? Seasons : []));
    }

    public Task<RequestOutcome> CreateAsync(DiscordRequestDraft draft, CancellationToken cancellationToken)
    {
        Drafts.Enqueue(draft);
        if (Outcome is { } outcome)
        {
            return Task.FromResult(outcome(draft));
        }

        var candidate = (draft.MediaType == MediaType.Movie ? Movies : Series).First(c => c.TmdbId == draft.TmdbId);
        return Task.FromResult(new RequestOutcome(RequestOutcomeKind.Created, new MediaRequest
        {
            Id = 1,
            MediaType = draft.MediaType,
            TmdbId = draft.TmdbId,
            Title = candidate.Title,
            Year = candidate.Year,
            PosterPath = candidate.PosterPath,
            Seasons = draft.Seasons,
            Status = draft.UserId is null ? RequestStatus.Pending : RequestStatus.Approved,
            Source = RequestSource.Discord,
            RequestedBy = draft.UserId,
            DiscordUserId = draft.DiscordUserId,
            DiscordName = draft.DiscordName,
        }));
    }
}

/// <summary>A migrated database in a temp folder with the Discord settings, links, users, link codes and the interaction handler.</summary>
public sealed class DiscordTestBed : IDisposable
{
    public const string GuildId = "100000000000000010";
    public const string RequestChannel = "100000000000000020";
    public const string OtherChannel = "100000000000000021";
    public const string FriendsRole = "100000000000000030";
    public const string JaneDiscordId = "100000000000000001";
    public const string GuestDiscordId = "100000000000000002";

    /// <summary>Shaped like a bot token, obviously not one.</summary>
    public const string Token = "fake-token-part-one-000000000000.Fake00.this-is-not-a-real-discord-bot-token-000";

    public DiscordTestBed()
    {
        Directory.CreateDirectory(DataDirectory);
        Database = new Database(new AppPaths(DataDirectory).DatabaseFile);
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Settings = new DiscordBotSettings(Config);
        Links = new DiscordLinkRepository(Database);
        Users = new UserRepository(Database);
        Requests = new RequestRepository(Database);
        LinkCodes = new DiscordLinkCodes(Time);
        Handler = new DiscordInteractionHandler(Settings, Api, Gateway, LinkCodes, Links, Users, Requests, Time, NullLogger<DiscordInteractionHandler>.Instance);
        JaneId = Users.Create("jane", "not-a-real-hash", UserRole.Friend);
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-discord-{Guid.NewGuid():N}");

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public Database Database { get; }

    public ConfigRepository Config { get; }

    public DiscordBotSettings Settings { get; }

    public DiscordLinkRepository Links { get; }

    public UserRepository Users { get; }

    public RequestRepository Requests { get; }

    public DiscordLinkCodes LinkCodes { get; }

    public FakeDiscordApi Api { get; } = new();

    public FakeRequestGateway Gateway { get; } = new();

    public DiscordBotStatusHolder Status { get; } = new();

    public InProcessEventBus Events { get; } = new();

    public DiscordInteractionHandler Handler { get; }

    public int JaneId { get; }

    public void Configure(DiscordSettingsChange change)
    {
        var errors = Settings.Validate(change);
        Assert.Empty(errors);
        Settings.Update(change);
    }

    public void LinkJane() => Links.Set(JaneId, JaneDiscordId, "Jane");

    private int _next;

    public DiscordInteraction Command(
        string name, IReadOnlyDictionary<string, string>? options = null, string? user = null, string? channel = RequestChannel, string? guild = GuildId, IReadOnlyList<string>? roles = null) =>
        new(
            $"1{Interlocked.Increment(ref _next):D17}",
            FakeDiscordApi.ApplicationId,
            DiscordInteraction.ApplicationCommand,
            "interaction-token",
            guild,
            channel,
            new DiscordUser(user ?? JaneDiscordId, user == GuestDiscordId ? "guest" : "jane", null),
            roles ?? [],
            name,
            options ?? new Dictionary<string, string>(),
            null,
            []);

    public DiscordInteraction Click(
        string customId, IReadOnlyList<string>? values = null, string? user = null, string? channel = RequestChannel, string? guild = GuildId, IReadOnlyList<string>? roles = null) =>
        new(
            $"2{Interlocked.Increment(ref _next):D17}",
            FakeDiscordApi.ApplicationId,
            DiscordInteraction.MessageComponent,
            "interaction-token",
            guild,
            channel,
            new DiscordUser(user ?? JaneDiscordId, user == GuestDiscordId ? "guest" : "jane", null),
            roles ?? [],
            null,
            new Dictionary<string, string>(),
            customId,
            values ?? []);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
