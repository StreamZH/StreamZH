// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core;
using StreamZH.Core.Downloads;
using StreamZH.Core.Events;
using StreamZH.Data;
using StreamZH.Library.Downloads;
using StreamZH.Library.Search;
using StreamZH.Usenet.Pool;
using StreamZH.Usenet.Yenc;

namespace StreamZH.Tests.Library.Downloads;

/// <summary>
/// Usenet in memory: yEnc encoded articles by message id, decoded on fetch like the real source does.
/// Articles can be missing, slow, blocked behind a gate, or the "server" can be down for a number of calls.
/// </summary>
public sealed class FakeArticleSource : IArticleSource, IArticleChecker
{
    public int ServerFailures;

    /// <summary>Message ids asked for with STAT (pre-check).</summary>
    public ConcurrentQueue<string> Checked { get; } = new();

    public Task<bool> ExistsAsync(string messageId, DateTimeOffset posted, CancellationToken cancellationToken)
    {
        Checked.Enqueue(messageId);
        return Task.FromResult(!Missing.ContainsKey(messageId) && Articles.ContainsKey(messageId));
    }

    public int MaxConnections { get; set; } = 4;
    public ConcurrentDictionary<string, byte[]> Articles { get; } = new();
    public ConcurrentDictionary<string, byte> Missing { get; } = new();
    public ConcurrentQueue<string> Fetched { get; } = new();
    public TimeSpan Delay { get; set; }

    /// <summary>Fetches for which this returns true wait for <see cref="Release"/> (or cancellation).</summary>
    public Func<string, bool>? Block { get; set; }

    public TaskCompletionSource Gate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => Gate.TrySetResult();

    public int Count(string messageId) => Fetched.Count(id => id == messageId);

    public async Task<DecodedArticle> FetchAsync(string messageId, IReadOnlyList<string> groups, DateTimeOffset posted, CancellationToken cancellationToken)
    {
        Fetched.Enqueue(messageId);
        if (Interlocked.Decrement(ref ServerFailures) >= 0)
        {
            throw new UsenetServerException("Connection refused");
        }

        if (Block?.Invoke(messageId) == true)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Missing.ContainsKey(messageId) || !Articles.TryGetValue(messageId, out var raw))
        {
            throw new ArticleNotFoundException(messageId);
        }

        var part = YencDecoder.Decode(raw);
        return new DecodedArticle(part.Name, part.FileSize, part.Offset, part.Data, part.IsValid);
    }
}

/// <summary>Records jobs; by default copies everything but par2 files into the target folder and succeeds.</summary>
public sealed class FakePostProcessor : IPostProcessor
{
    public ConcurrentQueue<PostProcessJob> Jobs { get; } = new();

    public Func<PostProcessJob, IProgress<PostProcessProgress>, CancellationToken, Task<PostProcessResult>>? Handler { get; set; }

    public Task<PostProcessResult> RunAsync(PostProcessJob job, IProgress<PostProcessProgress> progress, CancellationToken cancellationToken)
    {
        Jobs.Enqueue(job);
        return Handler is null ? Task.FromResult(CopyAll(job)) : Handler(job, progress, cancellationToken);
    }

    public static PostProcessResult CopyAll(PostProcessJob job)
    {
        Directory.CreateDirectory(job.TargetFolder);
        var files = new List<string>();
        foreach (var file in Directory.GetFiles(job.WorkFolder).Where(f => !f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(job.TargetFolder, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
            files.Add(target);
        }

        return new PostProcessResult(true, null, job.TargetFolder, files, false);
    }
}

public sealed class FakeFreeSpace : IFreeSpaceProvider
{
    public long? Free { get; set; } = 500L * 1024 * 1024 * 1024;

    public long? FreeBytes(string folder) => Free;
}

/// <summary>A release made of files with known content, its NZB and the articles registered in a <see cref="FakeArticleSource"/>.</summary>
public sealed class TestRelease
{
    public required string Title { get; init; }
    public required byte[] Nzb { get; init; }
    public required IReadOnlyList<TestFile> Files { get; init; }

    public TestFile this[string name] => Files.Single(f => f.Name == name);

    public IEnumerable<string> AllMessageIds => Files.SelectMany(f => f.MessageIds);
}

public sealed record TestFile(string Name, byte[] Content, IReadOnlyList<string> MessageIds);

public static class TestReleases
{
    /// <summary>Builds the files (deterministic content), yEnc encodes every segment and writes the NZB.</summary>
    public static TestRelease Build(FakeArticleSource source, string title, params (string Name, int Size, int SegmentSize)[] files)
    {
        var random = new Random(title.Length * 7919);
        var testFiles = new List<TestFile>();
        XNamespace ns = "http://www.newzbin.com/DTD/2003/nzb";
        var nzb = new XElement(ns + "nzb", new XElement(ns + "head", new XElement(ns + "meta", new XAttribute("type", "title"), title)));

        for (var f = 0; f < files.Length; f++)
        {
            var (name, size, segmentSize) = files[f];
            var content = new byte[size];
            random.NextBytes(content);
            var total = Math.Max(1, (size + segmentSize - 1) / segmentSize);
            var ids = new List<string>();
            var segments = new XElement(ns + "segments");
            for (var part = 0; part < total; part++)
            {
                var begin = part * segmentSize;
                var data = content.AsSpan(begin, Math.Min(segmentSize, size - begin)).ToArray();
                var id = $"{title}.{name}.{part + 1}@test";
                var article = EncodeYenc(data, name, part + 1, total, size, begin + 1);
                source.Articles[id] = article;
                ids.Add(id);
                segments.Add(new XElement(ns + "segment",
                    new XAttribute("bytes", article.Length), new XAttribute("number", part + 1), id));
            }

            nzb.Add(new XElement(ns + "file",
                new XAttribute("poster", "tester <t@example.com>"),
                new XAttribute("date", "1760000000"),
                new XAttribute("subject", $"[{f + 1}/{files.Length}] - \"{name}\" yEnc (1/{total})"),
                new XElement(ns + "groups", new XElement(ns + "group", "alt.binaries.test")),
                segments));
            testFiles.Add(new TestFile(name, content, ids));
        }

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), nzb).ToString();
        return new TestRelease { Title = title, Nzb = Encoding.UTF8.GetBytes(xml), Files = testFiles };
    }

    /// <summary>Multi-part yEnc article (=ybegin/=ypart/=yend with pcrc32). Escapes every critical character.</summary>
    public static byte[] EncodeYenc(byte[] data, string name, int part, int total, long fileSize, long begin)
    {
        var output = new List<byte>();
        output.AddRange(Encoding.Latin1.GetBytes(
            $"=ybegin part={part} total={total} line=128 size={fileSize} name={name}\r\n=ypart begin={begin} end={begin + data.Length - 1}\r\n"));
        var column = 0;
        foreach (var b in data)
        {
            var value = (byte)((b + 42) % 256);
            if (value is 0 or 9 or 10 or 13 or 32 or 46 or 61)
            {
                output.Add((byte)'=');
                value = (byte)((value + 64) % 256);
                column++;
            }

            output.Add(value);
            if (++column >= 128)
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
                column = 0;
            }
        }

        if (column > 0)
        {
            output.Add((byte)'\r');
            output.Add((byte)'\n');
        }

        var crc = Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);
        output.AddRange(Encoding.Latin1.GetBytes($"=yend size={data.Length} part={part} pcrc32={crc}\r\n"));
        return [.. output];
    }
}

/// <summary>A migrated database in a temp folder plus fakes for everything the engine talks to.</summary>
public sealed class DownloadTestBed : IDisposable
{
    private readonly List<DownloadEngine> _engines = [];

    public DownloadTestBed()
    {
        Directory.CreateDirectory(DataDirectory);
        Paths = new AppPaths(DataDirectory);
        Database = new Database(Paths.DatabaseFile);
        Database.Migrate();
        Config = new ConfigRepository(Database);
        Settings = new DownloadSettings(Config, Paths);
        Categories = new DownloadCategoryService(new DownloadCategoryRepository(Database), Settings, NullLogger<DownloadCategoryService>.Instance);
        Repository = new DownloadRepository(Database);
        History = new HistoryRepository(Database);
        Events.Subscribe<DownloadChanged>((e, _) =>
        {
            Changed.Enqueue(e.DownloadId);
            return ValueTask.CompletedTask;
        });
        Events.Subscribe<DownloadProgress>((e, _) =>
        {
            Progress.Enqueue(e);
            return ValueTask.CompletedTask;
        });
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"streamzh-dl-{Guid.NewGuid():N}");
    public AppPaths Paths { get; }
    public Database Database { get; }
    public ConfigRepository Config { get; }
    public DownloadSettings Settings { get; }
    public DownloadCategoryService Categories { get; }
    public DownloadRepository Repository { get; }
    public HistoryRepository History { get; }
    public InProcessEventBus Events { get; } = new();
    public FakeArticleSource Articles { get; } = new();
    public FakePostProcessor PostProcessor { get; } = new();
    public FakeFreeSpace FreeSpace { get; } = new();
    public DownloadEngineOptions Options { get; } = new() { Tick = TimeSpan.FromMilliseconds(50), ServerRetryDelay = TimeSpan.FromMilliseconds(100) };
    public ConcurrentQueue<int> Changed { get; } = new();
    public ConcurrentQueue<DownloadProgress> Progress { get; } = new();

    public DownloadEngine CreateEngine()
    {
        var engine = new DownloadEngine(
            Repository, History, Settings, Config, Articles, PostProcessor, Events, FreeSpace, Options, NullLogger<DownloadEngine>.Instance, Categories);
        _engines.Add(engine);
        return engine;
    }

    public async Task<DownloadEngine> StartAsync()
    {
        var engine = CreateEngine();
        await engine.StartAsync(CancellationToken.None);
        return engine;
    }

    public TestRelease Release(string title, params (string Name, int Size, int SegmentSize)[] files) =>
        TestReleases.Build(Articles, title, files);

    public static DownloadRequest Request(TestRelease release, DownloadCategory category = DownloadCategory.Movie, int? priority = 0, string? categoryName = null) =>
        new(release.Nzb, release.Title, category, MediaLink.None, priority, "Alpha", CategoryName: categoryName);

    public static async Task<DownloadItem> WaitForAsync(DownloadEngine engine, int id, DownloadStatus status, int timeoutMs = 20000)
    {
        DownloadItem? item = null;
        await WaitUntilAsync(() => (item = engine.Find(id))?.Status == status, timeoutMs, () => $"download {id} is {item?.Status} ({item?.Error}), expected {status}");
        return item!;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000, Func<string>? describe = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(describe?.Invoke() ?? "Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        Articles.Release();
        foreach (var engine in _engines)
        {
            engine.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            engine.Dispose();
        }

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
