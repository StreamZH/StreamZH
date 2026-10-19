// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Data.Migrations;
using StreamZH.Host.Security;
using StreamZH.Library;
using StreamZH.Library.Search;

namespace StreamZH.Tests.Library;

/// <summary>
/// 0.12, second verification (SV2): storage locations (N-03, N-06), the program folder (N-01), path identity (N-05),
/// hidden and reserved names (N-03) and the program folder's access list (N-02).
/// </summary>
public sealed class StorageLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("streamzh-storage-").FullName;

    private string Data => Path.Combine(_root, "data");

    private string App => Path.Combine(_root, "app");

    private string Media => Path.Combine(_root, "media");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AppPaths Paths(params string[] locations)
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(App);
        Directory.CreateDirectory(Media);
        var paths = new AppPaths(Data, App);
        paths.SetStorageLocations(locations);
        return paths;
    }

    /// <summary>N-03: an admin's folder outside every storage location is refused (the exploit used the service account's home, ~/.ssh).</summary>
    [Fact]
    public void Only_folders_inside_a_storage_location_may_be_used()
    {
        var paths = Paths(Media, Path.Combine(Data, "downloads"));
        var home = Path.Combine(_root, "home", ".ssh");

        Assert.Contains("outside the storage locations", paths.FolderProblem(home), StringComparison.Ordinal);
        Assert.Contains("outside the storage locations", paths.FolderProblem(_root), StringComparison.Ordinal);
        Assert.Null(paths.FolderProblem(Path.Combine(Media, "Movies")));
        Assert.Null(paths.FolderProblem(Media));
        Assert.Null(paths.FolderProblem(Path.Combine(Data, "downloads", "complete")));
        Assert.Contains("outside the storage locations", paths.FolderProblem(Media + "-other"), StringComparison.Ordinal);

        // None at all: everything is refused (fail closed); not loaded (unit tests): only the data and program folder rules.
        Assert.NotNull(Paths().FolderProblem(Path.Combine(Media, "Movies")));
        Assert.Null(new AppPaths(Data, App).FolderProblem(home));
    }

    /// <summary>N-03/N-05: a link inside a storage location may not lead out of it.</summary>
    [Fact]
    public void A_link_inside_a_storage_location_that_leads_out_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = Paths(Media);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "home", ".config", "autostart")).FullName;
        var link = Path.Combine(Media, "innocent");
        Directory.CreateSymbolicLink(link, outside);

        Assert.Contains("outside the storage locations", paths.FolderProblem(link), StringComparison.Ordinal);
        Assert.Contains("outside the storage locations", paths.FolderProblem(Path.Combine(link, "x.desktop")), StringComparison.Ordinal);
    }

    /// <summary>N-01: the folder StreamZH runs from is protected like the data folder, also when it is inside a storage location.</summary>
    [Fact]
    public void The_program_folder_is_never_a_download_or_media_folder()
    {
        var paths = Paths(_root);

        Assert.Contains("folder StreamZH runs from", paths.FolderProblem(App), StringComparison.Ordinal);
        Assert.Contains("folder StreamZH runs from", paths.FolderProblem(Path.Combine(App, "tools")), StringComparison.Ordinal);
        Assert.Contains("contains", paths.FolderProblem(_root), StringComparison.Ordinal);
        Assert.Null(paths.FolderProblem(Path.Combine(_root, "media", "Movies")));
        Assert.Throws<UnauthorizedAccessException>(() => paths.EnsureWritable(Path.Combine(App, "StreamZH.dll")));
    }

    /// <summary>N-05: path forms that hide what they point to are refused before anything compares them.</summary>
    [Theory]
    [InlineData(@"\\?\C:\ProgramData\StreamZH\keys")]
    [InlineData(@"\\.\C:\ProgramData\StreamZH\keys")]
    [InlineData(@"\??\C:\ProgramData\StreamZH")]
    [InlineData(@"//?/C:/ProgramData/StreamZH")]
    [InlineData(@"C:\PROGRA~3\StreamZH\keys")]
    [InlineData(@"C:\ProgramData\STREAM~1\keys")]
    [InlineData(@"D:\Media\..\..\ProgramData\StreamZH")]
    [InlineData(@"D:\Media\file.mkv:stream")]
    public void Windows_device_paths_short_names_and_dot_segments_are_refused(string path) =>
        Assert.NotNull(PathIdentity.SyntaxProblem(path, windows: true));

    [Theory]
    [InlineData(@"D:\Media\Movies")]
    [InlineData(@"\\nas\share\Movies")]
    [InlineData(@"D:\Media\Show~1080p")]
    [InlineData(@"D:\Media\Some.Movie.2021~Remux")]
    [InlineData(@"C:\")]
    public void Normal_windows_paths_pass(string path) => Assert.Null(PathIdentity.SyntaxProblem(path, windows: true));

    [Fact]
    public void Dot_segments_are_refused_on_every_system()
    {
        Assert.NotNull(PathIdentity.SyntaxProblem("/data/media/../../root/.ssh", windows: false));
        Assert.NotNull(PathIdentity.SyntaxProblem("/data/./media", windows: false));
        Assert.Null(PathIdentity.SyntaxProblem("/data/media/PROGRA~1", windows: false));
        Assert.NotNull(Paths(Media).FolderProblem(Path.Combine(Media, "..", "data", "keys")));
    }

    /// <summary>N-05: the same folder under another name is recognised by its device and inode numbers (Linux, macOS).</summary>
    [Fact]
    public void Folders_are_compared_by_identity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = Paths(_root);
        var keys = Directory.CreateDirectory(Path.Combine(Data, "keys")).FullName;
        var alias = Path.Combine(Media, "alias");
        Directory.CreateSymbolicLink(alias, keys);

        Assert.NotNull(PathIdentity.Identity(keys));
        Assert.Equal(PathIdentity.Identity(keys), PathIdentity.Identity(alias));
        Assert.NotEqual(PathIdentity.Identity(keys), PathIdentity.Identity(Media));
        Assert.Equal(keys, PathIdentity.Real(alias));
        Assert.Equal(Path.Combine(keys, "new", "x"), PathIdentity.Real(Path.Combine(alias, "new", "x")));
        Assert.Contains("data folder", paths.FolderProblem(Path.Combine(alias, "key-1.xml")), StringComparison.Ordinal);
    }

    /// <summary>N-03: names from release titles, NZB subjects and torrents never become hidden files or device names.</summary>
    [Theory]
    [InlineData(".ssh", "ssh")]
    [InlineData("..config", "config")]
    [InlineData(" .bashrc", "bashrc")]
    [InlineData("...", "release")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.nfo", "_nul.nfo")]
    [InlineData("COM1.Movie", "_COM1.Movie")]
    [InlineData("Console.Wars.2023.1080p", "Console.Wars.2023.1080p")]
    public void Names_never_start_with_a_dot_nor_are_device_names(string title, string expected) =>
        Assert.Equal(expected, NzbFileNames.SafeName(title));

    /// <summary>M029: an upgrade keeps working: every folder in use becomes a storage location; a new installation gets the download folder.</summary>
    [Fact]
    public void The_migration_allows_every_folder_in_use()
    {
        var database = new Database(Path.Combine(_root, "seed.db"));
        database.Migrate();
        using var connection = database.Open();
        Assert.Equal([Path.Combine(_root, "downloads")], JsonSerializer.Deserialize<List<string>>(new ConfigRepository(database).Get(ConfigKeys.StorageLocations)!));

        connection.Execute("DELETE FROM Config WHERE Key = @key", new { key = ConfigKeys.StorageLocations });
        connection.Execute("INSERT INTO RootFolders (Path, MediaType) VALUES ('/srv/media/movies', 1), ('/srv/media/series', 2), ('/mnt/other/films', 1)");
        connection.Execute("INSERT INTO Config (Key, Value) VALUES ('CompleteFolder', '/srv/downloads/complete'), ('RecycleBin', '/srv/trash/'), ('BackupFolder', '')");
        connection.Execute("UPDATE DownloadCategories SET Folder = '/mnt/usb/movies' WHERE Name = 'movies'");
        connection.Execute("UPDATE DownloadCategories SET Folder = 'relative' WHERE Name = 'series'");
        connection.Execute("INSERT INTO RemotePathMappings (Host, RemotePath, LocalPath) VALUES ('box', '/downloads', '/mnt/torrents')");

        var seeded = M029_StorageLocations.Seed(connection, null, "/var/lib/streamzh");

        Assert.Equal(
            ["/mnt/other/films", "/mnt/torrents", "/mnt/usb/movies", "/srv/downloads/complete", "/srv/media/movies", "/srv/media/series", "/srv/trash", "/var/lib/streamzh/downloads"],
            seeded.Select(p => p.Replace('\\', '/')));
        Assert.Equal(seeded, M029_StorageLocations.Seed(connection, null, "/var/lib/streamzh"));
        Assert.Equal(["/srv/media"], M029_StorageLocations.Reduce(["/srv/media/movies", "/srv/media", "/srv/media/series/", "/var/lib/streamzh/keys"], "/var/lib/streamzh"));
    }

    /// <summary>The owner's list: a folder inside a location is covered already; one around it replaces it; a used one stays.</summary>
    [Fact]
    public void Storage_locations_are_added_merged_and_kept_while_in_use()
    {
        var database = new Database(Path.Combine(_root, "service.db"));
        database.Migrate();
        var config = new ConfigRepository(database);
        var paths = new AppPaths(Data, App);
        StorageLocationService.Load(config, paths);
        var roots = new RootFolderRepository(database);
        var service = new StorageLocationService(config, paths, roots, new DownloadCategoryRepository(database), new RemotePathMappingRepository(database));
        var movies = Directory.CreateDirectory(Path.Combine(Media, "movies")).FullName;

        service.Add(movies);
        Assert.Contains("inside the storage location", Assert.Throws<ValidationFailedException>(() => service.Add(Path.Combine(movies, "x"))).Errors["path"][0], StringComparison.Ordinal);
        Assert.Throws<ValidationFailedException>(() => service.Add(Path.Combine(_root, "missing")));
        Assert.Throws<ValidationFailedException>(() => service.Add("relative/path"));
        service.Add(Media);
        Assert.Equal([Media], service.Locations.Where(l => !l.EndsWith("downloads", StringComparison.Ordinal)));
        Assert.Contains(Media, paths.StorageLocations!);

        roots.Add(movies, StreamZH.Core.Media.MediaType.Movie);
        var used = Assert.Throws<LibraryException>(() => service.Remove(Media));
        Assert.Contains(movies, used.Message, StringComparison.Ordinal);
        Assert.Equal(["Root folder: " + movies], service.All().Single(l => l.Path == Media).Uses);
    }

    /// <summary>N-02: the program folder must not be changeable by the broad groups (Authenticated Users get Modify below C:\).</summary>
    [Fact]
    public void A_program_folder_open_to_every_user_is_reported()
    {
        const int readExecute = 0x1200A9;
        var defaultBelowC = new[]
        {
            new AclEntry("S-1-5-18", AclEntry.FullControl, true, 3, 0),
            new AclEntry("S-1-5-32-544", AclEntry.FullControl, true, 3, 0),
            new AclEntry("S-1-5-32-545", readExecute, true, 3, 0),
            new AclEntry("S-1-5-11", AclEntry.Modify, true, 3, 2),
            new AclEntry("S-1-5-11", 0x4 | AclEntry.Synchronize, true, 0, 0),
        };
        Assert.Equal(["S-1-5-11"], AppFolderAcl.BroadWriters(defaultBelowC));

        var protectedByScript = new[]
        {
            new AclEntry("S-1-5-18", AclEntry.FullControl, true, 3, 0),
            new AclEntry("S-1-5-32-544", AclEntry.FullControl, true, 3, 0),
            new AclEntry("S-1-5-21-1-2-3-1001", AclEntry.Modify, true, 3, 0),
            new AclEntry("S-1-5-32-545", readExecute, true, 3, 0),
        };
        Assert.Empty(AppFolderAcl.BroadWriters(protectedByScript));
        Assert.Equal(["S-1-1-0"], AppFolderAcl.BroadWriters([new AclEntry("S-1-1-0", 0x10000000, true, 3, 0)]));
    }
}
