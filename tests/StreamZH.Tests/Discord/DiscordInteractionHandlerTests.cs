// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using StreamZH.Core.Media;
using StreamZH.Core.Requests;
using StreamZH.Library.Discord;

namespace StreamZH.Tests.Discord;

public sealed class DiscordInteractionHandlerTests : IDisposable
{
    private readonly DiscordTestBed _bed = new();

    public DiscordInteractionHandlerTests() =>
        _bed.Configure(new DiscordSettingsChange { Enabled = true, BotToken = DiscordTestBed.Token, ChannelIds = [DiscordTestBed.RequestChannel] });

    public void Dispose() => _bed.Dispose();

    private static Dictionary<string, string> Title(string title) => new() { [DiscordCommandSet.TitleOption] = title };

    private DiscordMessage LastEdit() => _bed.Api.Edits.Last();

    private DiscordInteractionResponse LastResponse() => _bed.Api.Responses.Last().Response;

    [Fact]
    public async Task A_linked_user_searches_picks_and_requests_a_movie()
    {
        _bed.LinkJane();

        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example")), default);

        // Deferred (ephemeral) first, then the results as embeds with a select menu.
        var defer = Assert.Single(_bed.Api.Responses).Response;
        Assert.Equal(5, defer.Type);
        Assert.Equal(DiscordMessage.Ephemeral, defer.Data!.Flags);
        var results = LastEdit();
        Assert.Equal(2, results.Embeds!.Count);
        Assert.Equal("https://image.tmdb.org/t/p/w342/poster603.jpg", results.Embeds[0].Thumbnail!.Url);
        Assert.Contains("\"example\"", results.Content, StringComparison.Ordinal);
        var select = results.Components![0].Components![0];
        Assert.Equal("szh:pick:m", select.CustomId);
        Assert.Equal(["603", "604"], select.Options!.Select(o => o.Value));
        Assert.Equal("Available", select.Options![1].Description);
        Assert.Equal("Example Movie *Reloaded* (2003)", select.Options[1].Label);

        await _bed.Handler.HandleAsync(_bed.Click("szh:pick:m", ["603"]), default);
        Assert.Equal(6, LastResponse().Type);
        var button = LastEdit().Components![0].Components![0];
        Assert.Equal("szh:req:m:603", button.CustomId);

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603"), default);
        var draft = Assert.Single(_bed.Gateway.Drafts);
        Assert.Equal(new DiscordRequestDraft(MediaType.Movie, 603, null, _bed.JaneId, DiscordTestBed.JaneDiscordId, "jane"), draft with { Seasons = null });
        var done = LastEdit();
        Assert.Contains("approved", done.Content, StringComparison.Ordinal);
        Assert.Empty(done.Components!);
    }

    [Fact]
    public async Task An_available_movie_cannot_be_requested_again()
    {
        _bed.LinkJane();
        await _bed.Handler.HandleAsync(_bed.Click("szh:pick:m", ["604"]), default);

        var message = LastEdit();
        Assert.Contains("already available", message.Content, StringComparison.Ordinal);
        Assert.Empty(message.Components!);
        Assert.Empty(_bed.Gateway.Drafts);
    }

    [Fact]
    public async Task Series_offer_only_open_seasons_and_request_the_chosen_ones()
    {
        _bed.LinkJane();
        await _bed.Handler.HandleAsync(_bed.Click("szh:pick:s", ["1399"]), default);

        var select = LastEdit().Components![0].Components![0];
        Assert.Equal("szh:seasons:1399", select.CustomId);

        // Specials, the available season 1 and the requested season 2 are not offered.
        Assert.Equal(["all", "3", "4"], select.Options!.Select(o => o.Value));
        Assert.Equal("Season 4", select.Options![2].Label);
        Assert.Equal(3, select.MaxValues);

        await _bed.Handler.HandleAsync(_bed.Click("szh:seasons:1399", ["3", "4"]), default);
        Assert.Equal([3, 4], Assert.Single(_bed.Gateway.Drafts).Seasons);
        Assert.Contains("Seasons 3, 4", LastEdit().Embeds![0].Footer!.Text, StringComparison.Ordinal);

        await _bed.Handler.HandleAsync(_bed.Click("szh:seasons:1399", ["all", "3"]), default);
        Assert.Null(_bed.Gateway.Drafts.Last().Seasons);
    }

    [Fact]
    public async Task Season_modes_from_the_settings_are_respected()
    {
        _bed.LinkJane();
        _bed.Configure(new DiscordSettingsChange { SeriesSeasons = DiscordSeasonModes.Single });
        await _bed.Handler.HandleAsync(_bed.Click("szh:pick:s", ["1399"]), default);
        var select = LastEdit().Components![0].Components![0];
        Assert.Equal(["3", "4"], select.Options!.Select(o => o.Value));
        Assert.Equal(1, select.MaxValues);

        // A forged multi-season click is refused.
        await _bed.Handler.HandleAsync(_bed.Click("szh:seasons:1399", ["3", "4"]), default);
        Assert.Empty(_bed.Gateway.Drafts);
        Assert.Equal(7, LastResponse().Type);

        _bed.Configure(new DiscordSettingsChange { SeriesSeasons = DiscordSeasonModes.All });
        await _bed.Handler.HandleAsync(_bed.Click("szh:pick:s", ["1399"]), default);
        var button = LastEdit().Components![0].Components![0];
        Assert.Equal("szh:req:s:1399", button.CustomId);
        await _bed.Handler.HandleAsync(_bed.Click(button.CustomId!), default);
        Assert.Null(Assert.Single(_bed.Gateway.Drafts).Seasons);
    }

    [Fact]
    public async Task Unlinked_users_are_refused_unless_guests_are_allowed()
    {
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example"), user: DiscordTestBed.GuestDiscordId), default);

        var reply = LastResponse();
        Assert.Equal(4, reply.Type);
        Assert.Equal(DiscordMessage.Ephemeral, reply.Data!.Flags);
        Assert.Contains("/link", reply.Data.Content, StringComparison.Ordinal);
        Assert.Empty(_bed.Api.Edits);
    }

    [Fact]
    public async Task Guests_need_one_of_the_roles_and_request_as_guests()
    {
        _bed.Configure(new DiscordSettingsChange { AllowGuests = true, GuestRoleIds = [DiscordTestBed.FriendsRole] });

        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example"), user: DiscordTestBed.GuestDiscordId, roles: ["100000000000000077"]), default);
        Assert.Contains($"<@&{DiscordTestBed.FriendsRole}>", LastResponse().Data!.Content, StringComparison.Ordinal);

        // Without the server there are no roles to check.
        _bed.Configure(new DiscordSettingsChange { AllowDirectMessages = true });
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example"), user: DiscordTestBed.GuestDiscordId, guild: null, channel: "100000000000000050"), default);
        Assert.Contains("only work in the server", LastResponse().Data!.Content, StringComparison.Ordinal);

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603", user: DiscordTestBed.GuestDiscordId, roles: [DiscordTestBed.FriendsRole]), default);
        var draft = Assert.Single(_bed.Gateway.Drafts);
        Assert.Null(draft.UserId);
        Assert.Equal(DiscordTestBed.GuestDiscordId, draft.DiscordUserId);
        Assert.Contains("An admin will look at it", LastEdit().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_only_work_in_the_configured_channels_and_not_in_direct_messages()
    {
        _bed.LinkJane();
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example"), channel: DiscordTestBed.OtherChannel), default);
        Assert.Contains($"<#{DiscordTestBed.RequestChannel}>", LastResponse().Data!.Content, StringComparison.Ordinal);

        await _bed.Handler.HandleAsync(_bed.Command("tv", Title("example"), guild: null, channel: "100000000000000050"), default);
        Assert.Contains("direct messages", LastResponse().Data!.Content, StringComparison.Ordinal);

        _bed.Configure(new DiscordSettingsChange { GuildIds = ["100000000000000011"] });
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example")), default);
        Assert.Contains("not set up for this server", LastResponse().Data!.Content, StringComparison.Ordinal);
        Assert.Empty(_bed.Api.Edits);
    }

    [Fact]
    public async Task Every_click_checks_the_permission_again()
    {
        _bed.LinkJane();
        _bed.Links.RemoveForUser(_bed.JaneId);

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603"), default);

        Assert.Empty(_bed.Gateway.Drafts);
        var update = LastResponse();
        Assert.Equal(7, update.Type);
        Assert.Contains("/link", update.Data!.Content, StringComparison.Ordinal);
        Assert.Empty(update.Data.Components!);
    }

    [Fact]
    public async Task A_disabled_account_cannot_request_or_fall_back_to_guest_access()
    {
        _bed.LinkJane();
        _bed.Configure(new DiscordSettingsChange { AllowGuests = true });
        var jane = _bed.Users.FindAccount(_bed.JaneId)!;
        _bed.Users.Update(jane with { Enabled = false });

        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example")), default);
        Assert.Contains("disabled", LastResponse().Data!.Content, StringComparison.Ordinal);

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603"), default);
        Assert.Empty(_bed.Gateway.Drafts);
    }

    [Theory]
    [InlineData(RequestOutcomeKind.LimitReached, "You can request 2 movies per 7 days; the next one on 21.10.2026.", "the next one on 21.10.2026")]
    [InlineData(RequestOutcomeKind.NotAllowed, null, "may not request")]
    [InlineData(RequestOutcomeKind.AlreadyAvailable, null, "already available")]
    public async Task Refused_requests_say_why(RequestOutcomeKind kind, string? message, string expected)
    {
        _bed.LinkJane();
        _bed.Gateway.Outcome = _ => new RequestOutcome(kind, null, message);

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603"), default);

        Assert.Contains(expected, LastEdit().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_open_request_is_reported_instead_of_a_duplicate()
    {
        _bed.LinkJane();
        _bed.Gateway.Outcome = d => new RequestOutcome(
            RequestOutcomeKind.AlreadyRequested, new MediaRequest { Title = "Example Movie", Year = 1999, MediaType = MediaType.Movie, TmdbId = 603, Status = RequestStatus.Processing });

        await _bed.Handler.HandleAsync(_bed.Click("szh:req:m:603"), default);

        Assert.Contains("already requested: being downloaded", LastEdit().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_problems_and_empty_results_are_shown_to_the_user()
    {
        _bed.LinkJane();
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("nothing")), default);
        Assert.Contains("Nothing found", LastEdit().Content, StringComparison.Ordinal);

        _bed.Gateway.SearchError = new RequestGatewayException("TMDB cannot be reached right now.");
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example")), default);
        Assert.Equal("TMDB cannot be reached right now.", LastEdit().Content);

        _bed.Gateway.SearchError = new InvalidOperationException("internal detail");
        await _bed.Handler.HandleAsync(_bed.Command("movie", Title("example")), default);
        Assert.DoesNotContain("internal detail", LastEdit().Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_codes_link_the_account_once_and_wrong_codes_are_limited()
    {
        var code = _bed.LinkCodes.Create(_bed.JaneId).Code;
        await _bed.Handler.HandleAsync(_bed.Command("link", new Dictionary<string, string> { ["code"] = code.ToLowerInvariant() }), default);
        Assert.Contains("Linked", LastResponse().Data!.Content, StringComparison.Ordinal);
        Assert.Equal(_bed.JaneId, _bed.Links.ForDiscord(DiscordTestBed.JaneDiscordId)!.UserId);

        // Used up.
        await _bed.Handler.HandleAsync(_bed.Command("link", new Dictionary<string, string> { ["code"] = code }), default);
        Assert.Contains("not valid", LastResponse().Data!.Content, StringComparison.Ordinal);

        // After five wrong codes even a valid one is refused for a while.
        for (var i = 0; i < DiscordInteractionHandler.MaxLinkAttempts; i++)
        {
            await _bed.Handler.HandleAsync(_bed.Command("link", new Dictionary<string, string> { ["code"] = "ZZZZZZ" }, user: DiscordTestBed.GuestDiscordId), default);
        }

        var fresh = _bed.LinkCodes.Create(_bed.JaneId).Code;
        await _bed.Handler.HandleAsync(_bed.Command("link", new Dictionary<string, string> { ["code"] = fresh }, user: DiscordTestBed.GuestDiscordId), default);
        Assert.Contains("Too many wrong codes", LastResponse().Data!.Content, StringComparison.Ordinal);

        _bed.Time.Advance(DiscordInteractionHandler.LinkAttemptWindow + TimeSpan.FromSeconds(1));
        fresh = _bed.LinkCodes.Create(_bed.JaneId).Code;
        await _bed.Handler.HandleAsync(_bed.Command("link", new Dictionary<string, string> { ["code"] = fresh }, user: DiscordTestBed.GuestDiscordId), default);
        Assert.Contains("Linked", LastResponse().Data!.Content, StringComparison.Ordinal);

        // The Discord account moved to the same user: the old link is gone.
        Assert.Null(_bed.Links.ForDiscord(DiscordTestBed.JaneDiscordId));
    }

    [Fact]
    public async Task Requests_lists_the_users_own_requests_from_web_and_discord()
    {
        _bed.LinkJane();
        var now = DateTime.UtcNow;
        _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 1, Title = "Web Movie", Year = 2020, RequestedBy = _bed.JaneId, Created = now, Updated = now });
        _bed.Requests.Add(new MediaRequest
        {
            MediaType = MediaType.Series, TmdbId = 2, Title = "Guest Show", Seasons = [2], DiscordUserId = DiscordTestBed.JaneDiscordId,
            Status = RequestStatus.Available, Source = RequestSource.Discord, Created = now.AddDays(-3), Updated = now,
        });
        _bed.Requests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 3, Title = "Someone Else", DiscordUserId = DiscordTestBed.GuestDiscordId, Created = now, Updated = now });

        await _bed.Handler.HandleAsync(_bed.Command("requests"), default);

        var embed = Assert.Single(LastResponse().Data!.Embeds!);
        Assert.Contains("Web Movie (2020)", embed.Description, StringComparison.Ordinal);
        Assert.Contains("Guest Show** · Season 2 · Available", embed.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Someone Else", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_explains_the_commands_and_the_link_state()
    {
        _bed.Configure(new DiscordSettingsChange { MovieCommand = "film" });
        await _bed.Handler.HandleAsync(_bed.Command("help", user: DiscordTestBed.GuestDiscordId), default);

        var embed = Assert.Single(LastResponse().Data!.Embeds!);
        Assert.Contains("/film", embed.Description, StringComparison.Ordinal);
        Assert.Contains("/link", embed.Footer!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Messages_never_ping_anybody_and_titles_cannot_format()
    {
        var json = JsonSerializer.Serialize(new DiscordMessage { Content = "x" }, DiscordJson.Options);
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", json, StringComparison.Ordinal);
        Assert.Equal("\\@everyone \\*bold\\*", DiscordText.Md("@everyone *bold*"));
    }

    [Fact]
    public void Commands_follow_the_settings()
    {
        var values = _bed.Settings.Values with { MovieCommand = string.Empty, AllowDirectMessages = true };
        var global = DiscordCommandSet.Build(values, global: true);
        Assert.Equal(["tv", "requests", "link", "help"], global.Select(c => c.Name));
        Assert.Equal([0, 1], global[0].Contexts);

        var guild = DiscordCommandSet.Build(values, global: false);
        Assert.All(guild, c => Assert.Null(c.Contexts));
        var json = JsonSerializer.Serialize(guild[0], DiscordJson.Options);
        Assert.Contains("\"max_length\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":1", json, StringComparison.Ordinal);
    }
}
