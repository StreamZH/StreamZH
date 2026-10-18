// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.CustomFormats;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library.Search;

public sealed class DecisionTargetFactoryTests : IDisposable
{
    private readonly SearchTestBed _bed = new();

    private DecisionTargetFactory Factory => new(_bed.Profiles);

    [Fact]
    public void Movie_without_file()
    {
        var target = Factory.ForMovie(_bed.Dune);

        Assert.Equal(MediaType.Movie, target.MediaType);
        Assert.Equal(("Dune", 2021, 155, 1), (target.Title, target.Year, target.RuntimeMinutes, target.EpisodeCount));
        Assert.Equal("Deutsch 1080p", target.Profile.Name);
        Assert.Equal(Language.English, target.OriginalLanguage);
        Assert.Null(target.ExistingQuality);
        Assert.Empty(target.ExistingLanguages);
        Assert.True(target.Monitored);
        Assert.Empty(target.BlocklistedTitles);
    }

    [Fact]
    public void Movie_with_file_reads_quality_and_languages_from_file_and_folder_names()
    {
        _bed.Dune.File = new MediaFile("Dune.2021.1080p.BluRay.x264-GRP.mkv", 10, DateTime.UtcNow);
        _bed.Dune.FolderPath = Path.Combine(_bed.DataDirectory, "media", "Dune (2021) German") + Path.DirectorySeparatorChar;
        _bed.Dune.Monitored = false;

        var target = Factory.ForMovie(_bed.Dune);

        Assert.Equal(Quality.Bluray1080p, target.ExistingQuality!.Quality);
        Assert.Equal([Language.German], target.ExistingLanguages);
        Assert.False(target.Monitored);
    }

    [Fact]
    public void Episodes_use_the_lowest_file_quality_only_when_every_episode_has_a_file()
    {
        _bed.Series.SetEpisodeFiles(_bed.Show.Id, new Dictionary<(int, int), MediaFile>
        {
            [(1, 1)] = new("Season 01/Breaking.Bad.S01E01.German.1080p.BluRay.x264-GRP.mkv", 1, DateTime.UtcNow),
            [(1, 2)] = new("Season 01/Breaking.Bad.S01E02.German.720p.WEB.h264-GRP.mkv", 1, DateTime.UtcNow),
        });
        var e1 = _bed.Episode(1, 1);
        var e2 = _bed.Episode(1, 2);
        var e3 = _bed.Episode(1, 3);

        var both = Factory.ForEpisodes(_bed.Show, [e1, e2]);
        Assert.Equal(MediaType.Series, both.MediaType);
        Assert.Equal(2, both.EpisodeCount);
        Assert.Equal(0, both.RuntimeMinutes);
        Assert.Equal(Quality.WebDl720p, both.ExistingQuality!.Quality);
        Assert.Equal([Language.German], both.ExistingLanguages);

        var withGap = Factory.ForEpisodes(_bed.Show, [e1, e2, e3]);
        Assert.Null(withGap.ExistingQuality);
        Assert.Equal(3, withGap.EpisodeCount);

        Assert.Equal(5, Factory.ForEpisodes(_bed.Show, [], episodeCount: 5).EpisodeCount);
    }

    [Fact]
    public void Episodes_are_monitored_when_the_series_and_one_episode_are()
    {
        _bed.Series.SetSeasonMonitored(_bed.Show.Id, 1, false);
        var episode = _bed.Episode(1, 1);
        Assert.False(Factory.ForEpisodes(_bed.Show, [episode]).Monitored);

        _bed.Series.SetEpisodeMonitored(episode.Id, true);
        Assert.True(Factory.ForEpisodes(_bed.Show, [_bed.Episode(1, 1), _bed.Episode(1, 2)]).Monitored);

        _bed.Show.Monitored = false;
        Assert.False(Factory.ForEpisodes(_bed.Show, [_bed.Episode(1, 1)]).Monitored);
    }

    [Fact]
    public void Missing_profile_is_reported()
    {
        _bed.Dune.QualityProfileId = 999;
        Assert.Equal(LibraryError.Invalid, Assert.Throws<LibraryException>(() => Factory.ForMovie(_bed.Dune)).Error);
    }

    [Fact]
    public void Targets_carry_the_formats_of_their_media_type_and_the_formats_of_the_file_on_disk()
    {
        var formats = new CustomFormatRepository(_bed.Database);
        var details = new MediaFileDetailsRepository(_bed.Database);
        var dv = formats.Add(CustomFormats.CustomFormatTestData.Movie("DV", 0, CustomFormats.CustomFormatTestData.Title(@"\bDV\b")));
        var group = formats.Add(CustomFormats.CustomFormatTestData.Movie(
            "Group", 0, CustomFormats.CustomFormatTestData.Spec(CustomFormatImplementations.ReleaseGroup, "^GOOD$")));
        formats.Add(CustomFormats.CustomFormatTestData.Format(MediaType.Series, "Season pack", 0, CustomFormats.CustomFormatTestData.Spec(CustomFormatImplementations.ReleaseType, 3)));

        // Renamed file: the stored release name and group still count (Radarr's file custom formats).
        _bed.Dune.FolderPath = Path.Combine(_bed.DataDirectory, "media", "Dune (2021)");
        _bed.Dune.File = new MediaFile("Dune (2021) WEBDL-2160p.mkv", 20L << 30, DateTime.UtcNow);
        details.Save(new MediaFileDetails(
            Path.Combine(_bed.Dune.FolderPath, _bed.Dune.File.RelativePath), Quality.WebDl2160p.Id, 1, 0, false, [Language.English.Id],
            "GOOD", "Dune.2021.2160p.WEB-DL.DV.H265-GOOD", null, "x265", null, DateTime.UtcNow));

        var factory = new DecisionTargetFactory(_bed.Profiles, fileDetails: details, customFormats: formats);
        var target = factory.ForMovie(_bed.Dune);

        Assert.Equal(["DV", "Group"], target.CustomFormats.Select(f => f.Name));
        Assert.Equal([dv, group], target.ExistingCustomFormats.Select(f => f.Id));
        Assert.Equal(Quality.WebDl2160p, target.ExistingQuality!.Quality);

        // Without stored details only the name counts.
        var nameOnly = new DecisionTargetFactory(_bed.Profiles, customFormats: formats).ForMovie(_bed.Dune);
        Assert.Empty(nameOnly.ExistingCustomFormats);

        var series = factory.ForEpisodes(_bed.Show, [_bed.Episode(1, 1)]);
        Assert.Equal(["Season pack"], series.CustomFormats.Select(f => f.Name));
        Assert.Empty(series.ExistingCustomFormats);
    }

    public void Dispose() => _bed.Dispose();
}
