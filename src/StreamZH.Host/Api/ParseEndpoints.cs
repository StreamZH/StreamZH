// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Parsing;
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
            QualityProfileRepository profiles,
            QualityDefinitionRepository definitions) =>
        {
            var parsed = ReleaseParser.Parse(title.Trim());
            if (parsed is null)
            {
                return Results.Ok(new ParseResult(null, null));
            }

            DecisionResult? decision = null;
            var size = (long)Math.Round((sizeMb ?? 0) * 1024 * 1024);

            if (movieId is { } mid && movies.Find(mid) is { } movie && profiles.Find(movie.QualityProfileId) is { } movieProfile)
            {
                var existingName = movie.File is null ? null : Path.GetFileName(movie.File.RelativePath) + " " + Path.GetFileName(movie.FolderPath);
                var target = new DecisionTarget(
                    MediaType.Movie, movie.Title, movie.Year, movie.Runtime, 1, movieProfile,
                    Language.FindById(movie.OriginalLanguageId),
                    existingName is null ? null : QualityParser.ParseQuality(existingName),
                    existingName is null ? [] : LanguageParser.ParseLanguages(Path.GetFileName(movie.FolderPath)),
                    Monitored: true,
                    BlocklistedTitles: new HashSet<string>());
                var match = ReleaseMatcher.Match(parsed, isSeries: false, [movie.Title, movie.OriginalTitle], movie.Year, movie.ImdbId, movie.TmdbId);
                decision = Decide(parsed, size, target, movieProfile.Name, $"{movie.Title} ({movie.Year})", match, definitions);
            }
            else if (seriesId is { } sid && series.Find(sid) is { } show && profiles.Find(show.QualityProfileId) is { } showProfile)
            {
                var episodes = parsed.FullSeason && parsed.SeasonNumber is { } season
                    ? series.Episodes(show.Id).Count(e => e.SeasonNumber == season)
                    : Math.Max(1, parsed.EpisodeNumbers.Count);
                var target = new DecisionTarget(
                    MediaType.Series, show.Title, show.Year, 0, episodes, showProfile,
                    Language.FindById(show.OriginalLanguageId),
                    null, [],
                    Monitored: true,
                    BlocklistedTitles: new HashSet<string>());
                var match = ReleaseMatcher.Match(parsed, isSeries: true, [show.Title, show.OriginalTitle], show.Year);
                decision = Decide(parsed, size, target, showProfile.Name, $"{show.Title} ({show.Year})", match, definitions);
            }

            return Results.Ok(new ParseResult(ParsedReleaseResource.From(parsed), decision));
        }).RequireAuthorization().WithTags("Parse");

        return app;
    }

    private static DecisionResult Decide(
        ParsedRelease parsed, long size, DecisionTarget target, string profile, string targetName, ReleaseMatch match, QualityDefinitionRepository definitions)
    {
        var candidate = new ReleaseCandidate(parsed, size, "Manual test", 25, DateTimeOffset.UtcNow, string.Empty, parsed.ReleaseTitle);
        var defs = definitions.All().ToDictionary(d => d.QualityId);
        var result = DecisionEngine.Evaluate([candidate], target, defs, DateTimeOffset.UtcNow).Single();

        // A search only ever offers releases that belong to the item; here the user can type anything, so say so first.
        List<RejectionResource> rejections = [];
        var named = parsed.Year > 0 ? $"{parsed.Title} ({parsed.Year})" : parsed.Title;
        var mismatch = match switch
        {
            ReleaseMatch.WrongType => parsed.IsEpisode ? $"This is a series release, {targetName} is a movie" : $"This is a movie release, {targetName} is a series",
            ReleaseMatch.WrongTitle => $"The release is for {named}, not {targetName}",
            ReleaseMatch.WrongYear => $"Same title, but the release year {parsed.Year} does not fit {targetName}",
            _ => null,
        };
        if (mismatch is not null)
        {
            rejections.Add(new RejectionResource(RejectionCodes.WrongMedia, mismatch));
        }

        rejections.AddRange(result.Rejections.Select(r => new RejectionResource(r.Code, r.Message)));
        return new DecisionResult(targetName, profile, rejections.Count == 0, rejections);
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
