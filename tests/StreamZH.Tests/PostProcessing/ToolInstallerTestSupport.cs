// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

/// <summary>Serves tool downloads from memory; unknown URLs get 404. Never touches the network.</summary>
public sealed class FakeToolDownloads : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    /// <summary>When set, every request waits for it (to test what happens while an installation runs).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public void Add(ToolDownload download, byte[] data) => _files[download.Url.AbsoluteUri] = data;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return _files.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Stands in for msiexec, 7zr.exe and tar: records the calls and lets the test create the unpacked files.</summary>
internal sealed class FakeToolRunner(Func<string, IReadOnlyList<string>, ToolProcessResult> run) : IToolProcessRunner
{
    private readonly List<(string FileName, IReadOnlyList<string> Arguments)> _calls = [];

    public IReadOnlyList<(string FileName, IReadOnlyList<string> Arguments)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add((fileName, arguments.ToList()));
        }

        return Task.FromResult(run(fileName, arguments));
    }
}

/// <summary>Small generated archives with their SHA-256, in place of the real (pinned) downloads.</summary>
internal static class TestToolPackages
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static ToolDownload Download(string fileName, byte[] data) =>
        new(fileName, new Uri("https://downloads.test/" + fileName), Convert.ToHexStringLower(SHA256.HashData(data)));

    /// <summary>A par2 package (zip) for the machine the tests run on.</summary>
    public static (ToolPackage Package, byte[] Data) Par2(string version = "1.5.0")
    {
        var executable = OperatingSystem.IsWindows() ? "par2.exe" : "par2";
        var data = Zip(($"par2cmdline-turbo-{version}/{executable}", "#!/bin/sh\necho par2cmdline-turbo version " + version + "\n"), ($"par2cmdline-turbo-{version}/COPYING", "GPL"));
        var download = Download($"par2-{version}.zip", data);
        return (new ToolPackage(PostProcessTool.Par2, ToolPackages.Par2Name, version, ToolPackages.Par2License, ToolInstallMethod.Zip, download, [executable]), data);
    }

    /// <summary>Creates files in a folder, as an unpacker would.</summary>
    public static void Create(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }
    }
}
