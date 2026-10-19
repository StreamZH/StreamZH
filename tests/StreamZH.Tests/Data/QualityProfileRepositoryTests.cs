// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Profiles;
using StreamZH.Core.Quality;
using StreamZH.Data;
using StreamZH.Library;

namespace StreamZH.Tests.Data;

public sealed class QualityProfileRepositoryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"streamzh-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void Seeds_default_profiles_and_size_limits()
    {
        var database = Migrated();
        var profiles = new QualityProfileRepository(database);

        Assert.Equal(["Any", "Deutsch 1080p", "Deutsch 2160p", "HD-1080p", "Ultra-HD"], profiles.All().Select(p => p.Name));

        // 1.0: a new installation starts with HD-1080p in any language; Deutsch 1080p is still there.
        var hd = profiles.Find(QualityDefaults.DefaultProfileId)!;
        Assert.Equal("HD-1080p", hd.Name);
        Assert.Equal(Language.Any.Id, hd.LanguageId);
        Assert.Equal(QualityDefaults.DefaultProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture), new ConfigRepository(database).Get(ConfigKeys.DefaultQualityProfileId));

        var german = profiles.Find(QualityDefaults.GermanProfileId)!;
        Assert.Equal("Deutsch 1080p", german.Name);
        Assert.Equal(Language.German.Id, german.LanguageId);
        Assert.True(german.PreferDualLanguage);
        Assert.Equal(Quality.Bluray1080p.Id, german.CutoffQualityId);
        Assert.Equal(Quality.All.Select(q => q.Id), german.Items.Select(i => i.QualityId));
        Assert.Equal(
            ["WEBDL-720p", "WEBRip-720p", "Bluray-720p", "WEBDL-1080p", "WEBRip-1080p", "Bluray-1080p", "Remux-1080p"],
            german.Items.Where(i => i.Allowed).Select(i => Quality.FindById(i.QualityId).Name));

        var any = profiles.FindByName("ANY")!;
        Assert.Equal(Language.Any.Id, any.LanguageId);
        Assert.False(any.IsAllowed(Quality.Cam.Id));
        Assert.False(any.IsAllowed(Quality.Unknown.Id));
        Assert.True(any.IsAllowed(Quality.Sdtv.Id));
        Assert.True(any.IsAllowed(Quality.RawHd.Id));

        var ultra = profiles.FindByName("Ultra-HD")!;
        Assert.Equal(Quality.Remux2160p.Id, ultra.CutoffQualityId);
        Assert.Equal(Language.Original.Id, ultra.LanguageId);


        var definitions = new QualityDefinitionRepository(database).All();
        Assert.Equal(Quality.All.Count, definitions.Count);
        Assert.Equal(new QualityDefinition(Quality.WebDl1080p.Id, "WEBDL-1080p", 0, 100, 95), definitions.Single(d => d.QualityId == Quality.WebDl1080p.Id));
        Assert.Equal(new QualityDefinition(Quality.Bluray1080p.Id, "Bluray-1080p", 0, null, null), definitions.Single(d => d.QualityId == Quality.Bluray1080p.Id));
        Assert.Equal(Quality.Unknown.Id, definitions[0].QualityId);
    }

    [Fact]
    public void Existing_movies_and_series_get_the_default_profile()
    {
        // A database from version 0.2 with one movie and one series.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _file }.ToString();
        using (var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(r => r.AddSQLite().WithGlobalConnectionString(connectionString).ScanIn(typeof(Database).Assembly).For.Migrations())
            .BuildServiceProvider(validateScopes: false))
        {
            services.GetRequiredService<IMigrationRunner>().MigrateUp(2);
        }

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Execute(
                """
                INSERT INTO RootFolders (Id, Path, MediaType) VALUES (1, '/media/movies', 1);
                INSERT INTO Movies (TmdbId, Title, OriginalTitle, SortTitle, Year, Runtime, Status, Genres, Monitored, RootFolderId, FolderPath, Added)
                VALUES (1, 'Dune', 'Dune', 'dune', 2021, 155, 'Released', '', 1, 1, '/media/movies/Dune (2021)', '2026-01-01');
                INSERT INTO Series (TmdbId, Title, OriginalTitle, SortTitle, Year, Status, Genres, Monitored, RootFolderId, FolderPath, Added)
                VALUES (2, 'Dark', 'Dark', 'dark', 2017, 'Ended', '', 1, 1, '/media/movies/Dark (2017)', '2026-01-01');
                """);
        }

        var database = Migrated();
        Assert.Equal(QualityDefaults.DefaultProfileId, Assert.Single(new MovieRepository(database).All()).QualityProfileId);
        Assert.Equal(QualityDefaults.DefaultProfileId, Assert.Single(new SeriesRepository(database).All()).QualityProfileId);
        Assert.Equal(2, new QualityProfileRepository(database).UsageCount(QualityDefaults.DefaultProfileId));
    }

    /// <summary>1.0: only new installations start with HD-1080p; migrating an existing database never touches its profiles or default.</summary>
    [Fact]
    public void Migrating_again_keeps_the_default_and_the_profiles_of_an_existing_installation()
    {
        var database = Migrated();
        var config = new ConfigRepository(database);
        var profiles = new QualityProfileRepository(database);
        config.Set(ConfigKeys.DefaultQualityProfileId, QualityDefaults.GermanProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        profiles.Update(profiles.Find(QualityDefaults.DefaultProfileId)! with { LanguageId = Language.Original.Id });

        database.Migrate();

        Assert.Equal(QualityDefaults.GermanProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture), config.Get(ConfigKeys.DefaultQualityProfileId));
        Assert.Equal(Language.Original.Id, profiles.Find(QualityDefaults.DefaultProfileId)!.LanguageId);
        Assert.Equal(5, profiles.Count());
    }

    [Fact]
    public void Service_validates_and_keeps_a_default()
    {
        var database = Migrated();
        var repository = new QualityProfileRepository(database);
        var config = new ConfigRepository(database);
        var service = new QualityProfileService(repository, config, NullLogger<QualityProfileService>.Instance);

        var invalid = Assert.Throws<ValidationFailedException>(() => service.Create(new QualityProfile
        {
            Name = " deutsch 1080P ",
            Items = [new QualityProfileItem(Quality.WebDl1080p.Id, true), new QualityProfileItem(999, true)],
            CutoffQualityId = Quality.Bluray1080p.Id,
            LanguageId = Language.Unknown.Id,
        }));
        Assert.Equal(["cutoffQualityId", "items", "languageId", "name"], invalid.Errors.Keys.Order());

        var created = service.Create(new QualityProfile
        {
            Name = "  SD  ",
            Items = [new QualityProfileItem(Quality.Sdtv.Id, true), new QualityProfileItem(Quality.Dvd.Id, true)],
            CutoffQualityId = Quality.Dvd.Id,
            LanguageId = Language.English.Id,
        });
        Assert.Equal("SD", created.Name);
        Assert.Equal(1, service.Resolve(1));
        Assert.Equal(QualityDefaults.DefaultProfileId, service.Resolve(null));
        Assert.Throws<LibraryException>(() => service.Resolve(12345));

        // Deleting the default moves the default to another profile.
        service.SetDefault(created.Id);
        service.Delete(created.Id);
        Assert.Equal(1, service.DefaultProfileId());
        Assert.Equal("1", config.Get(ConfigKeys.DefaultQualityProfileId));

        // The last profile stays.
        foreach (var id in repository.All().Select(p => p.Id).Where(id => id != 1))
        {
            service.Delete(id);
        }

        Assert.Equal(LibraryError.Conflict, Assert.Throws<LibraryException>(() => service.Delete(1)).Error);
        Assert.Equal(LibraryError.NotFound, Assert.Throws<LibraryException>(() => service.Delete(2)).Error);
    }

    [Fact]
    public void Language_check_accepts_any_original_and_real_languages()
    {
        Assert.True(QualityProfileService.IsValidLanguage(Language.Any.Id));
        Assert.True(QualityProfileService.IsValidLanguage(Language.Original.Id));
        Assert.True(QualityProfileService.IsValidLanguage(Language.German.Id));
        Assert.False(QualityProfileService.IsValidLanguage(Language.Unknown.Id));
        Assert.False(QualityProfileService.IsValidLanguage(-3));
        Assert.False(QualityProfileService.IsValidLanguage(999));
    }

    private Database Migrated()
    {
        var database = new Database(_file);
        database.Migrate();
        return database;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            File.Delete(file);
        }
    }
}
