// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Media;

namespace StreamZH.Host.Api;

public sealed record MediaFileResource(string RelativePath, long Size, DateTime Modified)
{
    public static MediaFileResource? From(MediaFile? file) => file is null ? null : new(file.RelativePath, file.Size, file.Modified);
}

public sealed record MovieResource(
    int Id, int TmdbId, string? ImdbId, string Title, string OriginalTitle, int Year, string? Overview, int Runtime, string Status,
    IReadOnlyList<string> Genres, string? PosterPath, string? BackdropPath, bool Monitored, int QualityProfileId, int RootFolderId, string FolderPath,
    DateTime Added, DateTime? LastRefreshed, bool HasFile, MediaFileResource? File, string OriginalLanguage)
{
    public static MovieResource From(Movie m) => new(
        m.Id, m.TmdbId, m.ImdbId, m.Title, m.OriginalTitle, m.Year, m.Overview, m.Runtime, m.Status, m.Genres, m.PosterPath, m.BackdropPath,
        m.Monitored, m.QualityProfileId, m.RootFolderId, m.FolderPath, m.Added, m.LastRefreshed, m.File is not null, MediaFileResource.From(m.File),
        StreamZH.Core.Quality.Language.FindById(m.OriginalLanguageId).Name);
}

public sealed record SeriesResource(
    int Id, int TmdbId, int? TvdbId, string? ImdbId, string Title, string OriginalTitle, int Year, string? Overview, string Status,
    string? Network, IReadOnlyList<string> Genres, string? PosterPath, string? BackdropPath, bool Monitored, int QualityProfileId,
    int RootFolderId, string FolderPath, DateTime Added, DateTime? LastRefreshed, int EpisodeCount, int AiredCount, int FileCount, long SizeOnDisk, string OriginalLanguage)
{
    public static SeriesResource From(Series s, SeriesCounts? c) => new(
        s.Id, s.TmdbId, s.TvdbId, s.ImdbId, s.Title, s.OriginalTitle, s.Year, s.Overview, s.Status, s.Network, s.Genres, s.PosterPath,
        s.BackdropPath, s.Monitored, s.QualityProfileId, s.RootFolderId, s.FolderPath, s.Added, s.LastRefreshed,
        c?.EpisodeCount ?? 0, c?.AiredCount ?? 0, c?.FileCount ?? 0, c?.SizeOnDisk ?? 0,
        StreamZH.Core.Quality.Language.FindById(s.OriginalLanguageId).Name);
}

public sealed record EpisodeResource(
    int Id, int SeasonNumber, int EpisodeNumber, string Title, string? AirDate, string? Overview, bool Monitored, bool HasFile, MediaFileResource? File)
{
    public static EpisodeResource From(Episode e) => new(
        e.Id, e.SeasonNumber, e.EpisodeNumber, e.Title, e.AirDate, e.Overview, e.Monitored, e.File is not null, MediaFileResource.From(e.File));
}

public sealed record SeasonResource(int SeasonNumber, int EpisodeCount, int FileCount, bool Monitored);

public sealed record SeriesDetailResource(SeriesResource Series, IReadOnlyList<SeasonResource> Seasons, IReadOnlyList<EpisodeResource> Episodes);

public sealed record LookupResource(int TmdbId, string Title, string OriginalTitle, int Year, string? Overview, string? PosterPath, int? LibraryId);

/// <param name="SearchForMissing">Start a search for the missing movie or episodes; null = the default from Settings → Indexers.</param>
public sealed record AddRequest(int TmdbId, int RootFolderId, bool Monitored = true, int? QualityProfileId = null, bool? SearchForMissing = null);

public sealed record MonitorRequest(bool Monitored);

public sealed record QualityProfileAssignment(int QualityProfileId);

public sealed record AddRootFolderRequest(string Path, MediaType MediaType);

public sealed record LibraryImportRequest(int RootFolderId, bool Monitored, IReadOnlyList<StreamZH.Library.ImportItem> Items, int? QualityProfileId = null);

public sealed record LibraryStats(int Movies, int MoviesWithFile, int Series, int Episodes, int EpisodesWithFile, long SizeOnDisk);