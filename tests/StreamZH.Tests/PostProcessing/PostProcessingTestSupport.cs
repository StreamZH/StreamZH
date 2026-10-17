// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Core.Downloads;
using StreamZH.Library.PostProcessing;

namespace StreamZH.Tests.PostProcessing;

/// <summary>Builds PAR 2.0 packets by hand, so the native reader is tested without par2 installed.</summary>
internal static class Par2Builder
{
    public static byte[] Packet(byte[] setId, ReadOnlySpan<byte> type, byte[] body)
    {
        var padded = Pad4(body);
        var packet = new byte[64 + padded.Length];
        "PAR2\0PKT"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        setId.CopyTo(packet, 32);
        type.CopyTo(packet.AsSpan(48));
        padded.CopyTo(packet, 64);
        MD5.HashData(packet.AsSpan(32)).CopyTo(packet, 16);
        return packet;
    }

    /// <summary>A complete index file plus <paramref name="recoveryBlocks"/> recovery packets (with dummy data).</summary>
    public static Par2Fixture Create(IReadOnlyList<(string Name, byte[] Data)> files, int sliceSize, int recoveryBlocks, bool unicodeNames = false)
    {
        var descriptions = files.Select(f =>
        {
            var md516K = MD5.HashData(f.Data.AsSpan(0, Math.Min(16384, f.Data.Length)));
            var nameBytes = Encoding.UTF8.GetBytes(f.Name);
            var idInput = new byte[16 + 8 + nameBytes.Length];
            md516K.CopyTo(idInput, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(idInput.AsSpan(16), (ulong)f.Data.Length);
            nameBytes.CopyTo(idInput, 24);
            return (f.Name, f.Data, Id: MD5.HashData(idInput), Md5: MD5.HashData(f.Data), Md516K: md516K, NameBytes: nameBytes);
        }).OrderBy(d => Convert.ToHexString(d.Id)).ToList();

        var main = new byte[12 + (16 * descriptions.Count)];
        BinaryPrimitives.WriteUInt64LittleEndian(main, (ulong)sliceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(8), (uint)descriptions.Count);
        for (var i = 0; i < descriptions.Count; i++)
        {
            descriptions[i].Id.CopyTo(main, 12 + (16 * i));
        }

        var setId = MD5.HashData(main);
        var packets = new List<byte[]> { Packet(setId, "PAR 2.0\0Main\0\0\0\0"u8, main) };

        foreach (var d in descriptions)
        {
            var body = new byte[56 + d.NameBytes.Length];
            d.Id.CopyTo(body, 0);
            d.Md5.CopyTo(body, 16);
            d.Md516K.CopyTo(body, 32);
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(48), (ulong)d.Data.Length);
            d.NameBytes.CopyTo(body, 56);
            packets.Add(Packet(setId, "PAR 2.0\0FileDesc"u8, unicodeNames ? Body(body.AsSpan(0, 56).ToArray(), "obfuscated-ascii"u8.ToArray()) : body));

            if (unicodeNames)
            {
                packets.Add(Packet(setId, "PAR 2.0\0UniFileN"u8, Body(d.Id, Encoding.Unicode.GetBytes(d.Name))));
            }

            var slices = (d.Data.Length + sliceSize - 1) / sliceSize;
            var ifsc = new byte[16 + (20 * slices)];
            d.Id.CopyTo(ifsc, 0);
            for (var s = 0; s < slices; s++)
            {
                var slice = new byte[sliceSize];
                var length = Math.Min(sliceSize, d.Data.Length - (s * sliceSize));
                Array.Copy(d.Data, s * sliceSize, slice, 0, length);
                MD5.HashData(slice).CopyTo(ifsc, 16 + (20 * s));
                BinaryPrimitives.WriteUInt32LittleEndian(ifsc.AsSpan(16 + (20 * s) + 16), System.IO.Hashing.Crc32.HashToUInt32(slice));
            }

            packets.Add(Packet(setId, "PAR 2.0\0IFSC\0\0\0\0"u8, ifsc));
        }

        packets.Add(Packet(setId, "PAR 2.0\0Creator\0"u8, Encoding.ASCII.GetBytes("StreamZH tests")));

        var recovery = new List<byte[]>();
        for (var e = 0; e < recoveryBlocks; e++)
        {
            var body = new byte[4 + sliceSize];
            BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)e);
            Random.Shared.NextBytes(body.AsSpan(4));
            recovery.Add(Packet(setId, "PAR 2.0\0RecvSlic"u8, body));
        }

        return new Par2Fixture(Convert.ToHexStringLower(setId), packets.SelectMany(p => p).ToArray(), recovery);
    }

    private static byte[] Body(byte[] first, byte[] second) => [.. first, .. second];

    private static byte[] Pad4(byte[] body) => body.Length % 4 == 0 ? body : [.. body, .. new byte[4 - (body.Length % 4)]];
}

/// <param name="Index">The .par2 index file (no recovery data).</param>
/// <param name="Recovery">One packet per recovery block.</param>
internal sealed record Par2Fixture(string SetId, byte[] Index, IReadOnlyList<byte[]> Recovery)
{
    public byte[] Volume(int from, int count) => Recovery.Skip(from).Take(count).SelectMany(p => p).ToArray();
}

/// <summary>A temporary folder that is removed after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"streamzh-pp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, byte[] data)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class TestData
{
    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static void Corrupt(string path, long offset, int count = 8)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        for (var i = 0; i < count; i++)
        {
            bytes[i] ^= 0xFF;
        }

        stream.Position = offset;
        stream.Write(bytes);
    }
}

/// <summary>Finds par2, 7z and rar on this machine for the integration tests.</summary>
internal static class InstalledTools
{
    public static string? Par2 { get; } = Find("par2");

    public static string? SevenZip { get; } = Find("7z") ?? Find("7zz") ?? Find("7za");

    public static string? Rar { get; } = Find("rar");

    public static string? Find(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe" } : [name];
        var extra = OperatingSystem.IsWindows() && name == "7z" ? new[] { @"C:\Program Files\7-Zip" } : [];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(extra)
            .SelectMany(dir => names.Select(n => System.IO.Path.Combine(dir, n)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Runs a tool to create test data; fails the test when it does not work.</summary>
    public static void Run(string tool, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} {string.Join(' ', arguments)} failed ({process.ExitCode}): {output.Result} {error.Result}");
    }
}

/// <summary>[Fact] that is skipped with a clear reason when par2 / 7-Zip / rar are not installed.</summary>
public sealed class RequiresToolsFactAttribute : FactAttribute
{
    public RequiresToolsFactAttribute(params string[] tools)
    {
        var missing = tools.Where(t => t switch
        {
            "par2" => InstalledTools.Par2 is null,
            "7z" => InstalledTools.SevenZip is null,
            "rar" => InstalledTools.Rar is null,
            _ => true,
        }).ToList();

        if (missing.Count > 0)
        {
            Skip = $"Needs {string.Join(" and ", missing)} on PATH (Linux: apt install par2 p7zip-full rar; Windows: 7-Zip from 7-zip.org, par2cmdline from GitHub).";
        }
    }
}

/// <summary>Settings with fixed paths (null = automatic).</summary>
internal sealed class FixedToolSettings(string? par2 = null, string? sevenZip = null) : IPostProcessToolSettings
{
    public string? Par2Path { get; set; } = par2;

    public string? SevenZipPath { get; set; } = sevenZip;
}

/// <summary>Collects progress reports in order.</summary>
internal sealed class ProgressLog : IProgress<PostProcessProgress>
{
    private readonly List<PostProcessProgress> _items = [];

    public IReadOnlyList<PostProcessProgress> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }
    }

    public void Report(PostProcessProgress value)
    {
        lock (_items)
        {
            _items.Add(value);
        }
    }
}

internal static class PostProcessorFactory
{
    /// <summary>A post-processor that uses the tools on this machine (or none at all with <paramref name="noTools"/>).</summary>
    public static PostProcessor Create(bool noTools = false)
    {
        var runner = new ToolProcessRunner();
        var environment = noTools
            ? new ToolEnvironment("/nonexistent-app", string.Empty, OperatingSystem.IsWindows(), [])
            : ToolEnvironment.Current;
        var tools = new PostProcessTools(new FixedToolSettings(), runner, environment);
        return new PostProcessor(tools, runner, NullLogger<PostProcessor>.Instance);
    }

    public static PostProcessJob Job(TempFolder folder, string title = "Some.Movie.2024.1080p.WEB-DL", string? password = null) =>
        new(1, title, folder.Combine("work"), folder.Combine("done", "Some.Movie"), password);
}
