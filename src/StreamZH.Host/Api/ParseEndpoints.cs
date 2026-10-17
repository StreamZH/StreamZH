// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Parsing.Releases;

namespace StreamZH.Host.Api;

/// <summary>
/// Test tool: how does StreamZH read a release name, and would it grab it for a given movie or series?
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
            MovieRepository movies,
            SeriesRepository series,
            DecisionTargetFactory targets,
            QualityDefinitionRepository definitions) =>
        {
            var parsed = ReleaseParser.Parse(title.Trim());
            if (parsed is null)
            {
                return Results.Ok(new ParseResult(null, null));
            }

            DecisionResult? decision = null;
            var size = (long)Math.Round((sizeMb ?? 0) * 1024 * 1024);

            if (movieId is { } mid && movies.Find(mid) is { } movie)
            {
                var name = $"{movie.Title} ({movie.Year})";
                var target = targets.ForMovie(movie) with { Monitored = true };
                var mismatch = ReleaseMatching.MismatchRejection(ReleaseMatching.MatchMovie(parsed, movie), parsed, name);
                decision = Decide(parsed, size, target, name, mismatch, definitions);
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
                decision = Decide(parsed, size, target, name, mismatch, definitions);
            }

            return Results.Ok(new ParseResult(ParsedReleaseResource.From(parsed), decision));
        }).RequireAuthorization().WithTags("Parse");

        return app;
    }

    private static DecisionResult Decide(
        ParsedRelease parsed, long size, DecisionTarget target, string targetName, Rejection? mismatch, QualityDefinitionRepository definitions)
    {
        // A release for another item only gets that one reason: quality or size rules say nothing useful about it.
        if (mismatch is not null)
        {
            return new DecisionResult(targetName, target.Profile.Name, false, [new RejectionResource(mismatch.Code, mismatch.Message)]);
        }

        var candidate = new ReleaseCandidate(parsed, size, "Manual test", 25, DateTimeOffset.UtcNow, string.Empty, parsed.ReleaseTitle);
        var defs = definitions.All().ToDictionary(d => d.QualityId);
        var result = DecisionEngine.Evaluate([candidate], target, defs, DateTimeOffset.UtcNow).Single();
        return new DecisionResult(targetName, target.Profile.Name, result.Accepted, [.. result.Rejections.Select(r => new RejectionResource(r.Code, r.Message))]);
    }
}

public sealed record ParseResult(ParsedReleaseResource? Parsed, DecisionResult? Decision);

public sealed record DecisionResult(string Target, string Profile, bool Accepted, IReadOnlyList<RejectionResource> Rejections);

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
