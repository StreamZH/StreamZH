// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;

namespace StreamZH.Host.Api;

/// <summary>
/// Test tool: how does StreamZH read a release name, which release profiles match (and the preferred score), and would
/// it grab it for a given movie or series? An optional indexer makes indexer-specific release profiles apply.
/// </summary>
public static class ParseEndpoints
{
    public static IEndpointRouteBuilder MapParseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/parse", (
            string title,
            int? movieId,
            int? seriesId,
            double? sizeMb,
            int? indexerId,
            MovieRepository movies,
            SeriesRepository series,
            DecisionTargetFactory targets,
            QualityDefinitionRepository definitions,
            IndexerOptions options) =>
        {
            var parsed = ReleaseParser.Parse(title.Trim());
            if (parsed is null)
            {
                return Results.Ok(new ParseResult(null, null));
            }

            DecisionResult? decision = null;
            DecisionTarget? decided = null;
            var size = (long)Math.Round((sizeMb ?? 0) * 1024 * 1024);
            var rules = options.Rules();
            var indexer = indexerId ?? 0;

            if (movieId is { } mid && movies.Find(mid) is { } movie)
            {
                var name = $"{movie.Title} ({movie.Year})";
                var target = targets.ForMovie(movie) with { Monitored = true };
                var mismatch = ReleaseMatching.MismatchRejection(ReleaseMatching.MatchMovie(parsed, movie), parsed, name);
                decision = Decide(parsed, size, indexer, target, name, mismatch, definitions, rules);
                decided = target;
            }
            else if (seriesId is { } sid && series.Find(sid) is { } show)
            {
                var name = $"{show.Title} ({show.Year})";
                var library = series.Episodes(show.Id);
                IReadOnlyList<Episode> covered = parsed.IsEpisode ? ReleaseMatching.MapEpisodes(parsed, library).Episodes : [];

                // Episodes that are not in the library (yet): count them from the name.
                int? count = covered.Count > 0 ? null
                    : parsed.FullSeason && parsed.SeasonNumber is { } season ? Math.Max(1, library.Count(e => e.SeasonNumber == season))
                    : Math.Max(1, parsed.EpisodeNumbers.Count);
                var target = targets.ForEpisodes(show, covered, count) with { Monitored = true };
                var mismatch = ReleaseMatching.MismatchRejection(ReleaseMatching.MatchSeries(parsed, show), parsed, name);
                decision = Decide(parsed, size, indexer, target, name, mismatch, definitions, rules);
                decided = target;
            }

            // Without a movie or series the quality profile is unknown: show every enabled profile, whatever it is limited to.
            var explained = decided is null
                ? ReleaseProfileRules.Explain(
                    rules with { ReleaseProfiles = [.. rules.ReleaseProfiles.Select(p => p with { IndexerIds = [], QualityProfileIds = [] })] },
                    indexer, 0, parsed.ReleaseTitle)
                : ReleaseProfileRules.Explain(rules, indexer, decided.Profile.Id, parsed.ReleaseTitle);
            var original = rules.ReleaseProfiles.ToDictionary(p => p.Id);

            // The formats of the item's media type and its profile's score; without an item the type the name looks like, no score.
            var formats = decided is not null
                ? new DecisionContext(decided, new Dictionary<int, StreamZH.Core.Profiles.QualityDefinition>(), DateTimeOffset.UtcNow)
                    .CustomFormatsFor(new ReleaseCandidate(parsed, size, "Manual test", 25, default, string.Empty, parsed.ReleaseTitle) { IndexerId = indexer })
                : CustomFormatResult.None with
                {
                    Formats = CustomFormatCalculator.Matching(
                        targets.Formats(parsed.IsEpisode ? MediaType.Series : MediaType.Movie),
                        CustomFormatInput.ForRelease(parsed, size, parsed.IsEpisode ? MediaType.Series : MediaType.Movie, Language.Unknown)),
                };
            return Results.Ok(new ParseResult(ParsedReleaseResource.From(parsed), decision)
            {
                CustomFormats = CustomFormatRefResource.From(formats.Formats),
                CustomFormatScore = formats.Score,
                ReleaseProfiles = [.. explained.Select(m => ReleaseProfileMatchResource.From(m, original.GetValueOrDefault(m.Profile.Id) ?? m.Profile))],
                PreferredScore = explained.Sum(m => m.Score),
                HardcodedSubs = HardcodedSubs.Find(parsed.ReleaseTitle),
            });
        }).RequireAuthorization().WithTags("Parse");

        return app;
    }

    private static DecisionResult Decide(
        ParsedRelease parsed, long size, int indexerId, DecisionTarget target, string targetName, Rejection? mismatch,
        QualityDefinitionRepository definitions, ReleaseRules rules)
    {
        // A release for another item only gets that one reason: quality or size rules say nothing useful about it.
        if (mismatch is not null)
        {
            return new DecisionResult(targetName, target.Profile.Name, false, [new RejectionResource(mismatch.Code, mismatch.Message)]);
        }

        // No publish date: the minimum age and the retention say nothing about a pasted name.
        var candidate = new ReleaseCandidate(parsed, size, "Manual test", 25, default, string.Empty, parsed.ReleaseTitle) { IndexerId = indexerId };
        var defs = definitions.All().ToDictionary(d => d.QualityId);
        var result = DecisionEngine.Evaluate([candidate], target, defs, DateTimeOffset.UtcNow, rules).Single();
        return new DecisionResult(targetName, target.Profile.Name, result.Accepted, [.. result.Rejections.Select(r => new RejectionResource(r.Code, r.Message))])
        {
            PreferredScore = result.PreferredScore,
            CustomFormats = CustomFormatRefResource.From(result.CustomFormats),
            CustomFormatScore = result.CustomFormatScore,
        };
    }
}

/// <summary>
/// <see cref="ReleaseProfiles"/>: the enabled release profiles that match the name (or miss a "must contain" term),
/// <see cref="PreferredScore"/> their summed score, <see cref="HardcodedSubs"/> the hardcoded-subs tag found, if any.
/// </summary>
public sealed record ParseResult(ParsedReleaseResource? Parsed, DecisionResult? Decision)
{
    public IReadOnlyList<ReleaseProfileMatchResource> ReleaseProfiles { get; init; } = [];

    /// <summary>Custom formats the name matches (of the item's media type, else of the type the name looks like).</summary>
    public IReadOnlyList<CustomFormatRefResource> CustomFormats { get; init; } = [];

    /// <summary>Their score in the item's quality profile; 0 without an item.</summary>
    public int CustomFormatScore { get; init; }

    public int PreferredScore { get; init; }

    public string? HardcodedSubs { get; init; }
}

public sealed record DecisionResult(string Target, string Profile, bool Accepted, IReadOnlyList<RejectionResource> Rejections)
{
    public int PreferredScore { get; init; }

    public IReadOnlyList<CustomFormatRefResource> CustomFormats { get; init; } = [];

    public int CustomFormatScore { get; init; }
}

/// <summary>What one release profile says about a name. <see cref="Limited"/>: it only applies to some indexers or quality profiles.</summary>
public sealed record ReleaseProfileMatchResource(
    int Id,
    string Name,
    bool Rejects,
    bool MissingRequired,
    IReadOnlyList<string> Required,
    IReadOnlyList<string> RequiredMatched,
    IReadOnlyList<string> IgnoredMatched,
    IReadOnlyList<PreferredTermResource> PreferredMatched,
    int Score,
    bool Limited)
{
    public static ReleaseProfileMatchResource From(ReleaseProfileMatch m, ReleaseProfile original) => new(
        m.Profile.Id, m.Profile.Name, m.Rejects, m.MissingRequired, m.Profile.Required, m.RequiredMatched, m.IgnoredMatched,
        [.. m.PreferredMatched.Select(p => new PreferredTermResource(p.Term, p.Score))], m.Score,
        original.IndexerIds.Count > 0 || original.QualityProfileIds.Count > 0);
}

public sealed record RejectionResource(string Code, string Message);

public sealed record ParsedReleaseResource(
    string Kind, string Title, string CleanTitle, int Year, int? SeasonNumber, IReadOnlyList<int> EpisodeNumbers,
    IReadOnlyList<int> AbsoluteEpisodeNumbers, string? AirDate, bool FullSeason, bool MultiSeason, bool IsSpecial,
    string Quality, int Version, int Real, bool IsRepack, IReadOnlyList<string> Languages, string? ReleaseGroup,
    string? Edition, HdrFormat Hdr, string? VideoCodec, string? AudioCodec, string? ImdbId, int? TmdbId)
{
    public static ParsedReleaseResource From(ParsedRelease p) => new(
        p.IsEpisode ? "Episode" : "Movie", p.Title, p.CleanTitle, p.Year, p.SeasonNumber, p.EpisodeNumbers, p.AbsoluteEpisodeNumbers,
        p.AirDate, p.FullSeason, p.MultiSeason, p.IsSpecial, p.Quality.Quality.Name, p.Quality.Revision.Version, p.Quality.Revision.Real,
        p.Quality.Revision.IsRepack, [.. p.Languages.Select(l => l.Name)], p.ReleaseGroup, p.Edition, p.Hdr, p.VideoCodec, p.AudioCodec,
        p.ImdbId, p.TmdbId);
}
