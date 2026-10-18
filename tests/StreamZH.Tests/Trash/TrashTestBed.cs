// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Data;
using StreamZH.Library;
using StreamZH.Library.Import;
using StreamZH.Library.Trash;
using StreamZH.Tests.Library.ImportLists;

namespace StreamZH.Tests.Trash;

/// <summary>A database with the profile, Media Management and TRaSH services; the guides come from the fixture subset (no network).</summary>
internal sealed class TrashTestBed : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A subset of the TRaSH Guides (MIT): the [German] HD Bluray + WEB, HD Bluray + WEB and WEB-1080p templates with their formats and groups.</summary>
    public static string GuidesFolder => Path.Combine(AppContext.BaseDirectory, "Trash", "Guides");

    public const string MovieGerman = "2b90e905c99490edc7c7a5787443748b";
    public const string SeriesGerman = "dca7e5e9e99c703bcbdaaa471dd40e98";

    private static readonly Lazy<TrashGuides> ParsedGuides = new(() => TrashGuideParser.Parse(GuidesFolder));

    public TrashTestBed()
    {
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Database = new Database(Paths.DatabaseFile);
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Formats = new CustomFormatRepository(Database);
        Profiles = new QualityProfileRepository(Database);
        Definitions = new QualityDefinitionRepository(Database);
        Trash = new TrashRepository(Database);
        Settings = new TrashSettings(Config);
        Media = new MediaManagementSettings(Config);
        ProfileService = new QualityProfileService(Profiles, Config, NullLogger<QualityProfileService>.Instance, [new TrashProfileObserver(Trash)]);
        Apply = new TrashApplyService(
            Formats, Profiles, ProfileService, new QualityDefinitionService(Definitions, NullLogger<QualityDefinitionService>.Instance), Media, Trash,
            Settings, Config, NullLogger<TrashApplyService>.Instance);
        Store = new TrashGuideStore(Paths, Config, Settings, new FakeHttpClientFactory(Http), new FixedTime(Now), NullLogger<TrashGuideStore>.Instance);
        Sync = new TrashSyncService(Store, Apply, Settings, Profiles, Trash, NullLogger<TrashSyncService>.Instance);
        Recyclarr = new RecyclarrImporter(Apply, Profiles, ProfileService, Formats, NullLogger<RecyclarrImporter>.Instance);
    }

    public static TrashGuides Guides => ParsedGuides.Value;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-trash-{Guid.NewGuid():N}");

    public AppPaths Paths { get; }
    public Database Database { get; }
    public ConfigRepository Config { get; }
    public CustomFormatRepository Formats { get; }
    public QualityProfileRepository Profiles { get; }
    public QualityDefinitionRepository Definitions { get; }
    public TrashRepository Trash { get; }
    public TrashSettings Settings { get; }
    public MediaManagementSettings Media { get; }
    public QualityProfileService ProfileService { get; }
    public TrashApplyService Apply { get; }
    public FakeGitHub Http { get; } = new();
    public TrashGuideStore Store { get; }
    public TrashSyncService Sync { get; }
    public RecyclarrImporter Recyclarr { get; }

    public TrashApplyResult ApplyGerman(int? profileId = null, string? name = null, TrashGuides? guides = null) =>
        Apply.Apply(new TrashApplyRequest { ProfileId = profileId, Name = name, MovieTemplate = MovieGerman, SeriesTemplate = SeriesGerman }, guides ?? Guides);

    /// <summary>Points the settings at the fixture as a local repository and makes it the current copy.</summary>
    public async Task UseLocalCopyAsync()
    {
        Settings.Save(TrashSettings.Defaults with { Repository = GuidesFolder });
        var result = await Store.DownloadAsync(CancellationToken.None);
        Assert.Null(result.Error);
    }

    public void Dispose()
    {
        SqliteCleanup();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder; removed by the OS.
        }
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>The fixture as a GitHub branch archive ("Guides-master/…", commit as zip comment, like GitHub does).</summary>
    public static byte[] ArchiveOfFixture(string commit, bool withImages = true)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.Comment = commit;
            foreach (var file in Directory.EnumerateFiles(GuidesFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(GuidesFolder, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, "Guides-master/" + relative);
            }

            if (withImages)
            {
                using (var stream = zip.CreateEntry("Guides-master/docs/images/logo.png").Open())
                {
                    stream.Write(Encoding.ASCII.GetBytes("not needed"));
                }

                using (var stream = zip.CreateEntry("Guides-master/docs/json/../../../escape.json").Open())
                {
                    stream.Write(Encoding.ASCII.GetBytes("{}"));
                }
            }
        }

        return memory.ToArray();
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Answers the GitHub commit API and the archive download; records the requests.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Commit the API reports; null = the API fails (rate limit).</summary>
    public string? ApiCommit { get; set; }

    public byte[]? Archive { get; set; }

    public string ETag { get; set; } = "\"etag-1\"";

    public HttpStatusCode? ArchiveStatus { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            return Task.FromResult(ApiCommit is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiCommit) });
        }

        if (ArchiveStatus is { } status)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        if (Archive is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (request.Headers.IfNoneMatch.Any(t => t.ToString() == ETag))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(ETag);
        return Task.FromResult(response);
    }
}
