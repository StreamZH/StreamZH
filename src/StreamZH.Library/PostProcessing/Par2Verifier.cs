// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;

namespace StreamZH.Library.PostProcessing;

public enum Par2FileState
{
    Ok,
    Damaged,
    Missing,
}

/// <param name="Path">Where the file is (or should be) on disk.</param>
/// <param name="DamagedSlices">Slices that must be rebuilt (all of them for a missing file).</param>
/// <param name="RenamedFrom">Obfuscated name the file had before it got its real name back.</param>
public sealed record Par2FileStatus(string Name, string Path, Par2FileState State, int TotalSlices, int DamagedSlices, string? RenamedFrom);

public sealed record Par2VerifyResult(Par2Set Set, IReadOnlyList<Par2FileStatus> Files)
{
    public bool NeedsRepair => Files.Any(f => f.State != Par2FileState.Ok);

    public int BlocksNeeded => Files.Sum(f => f.DamagedSlices);

    public int BlocksAvailable => Set.RecoveryBlocks;

    public bool CanRepair => BlocksNeeded <= BlocksAvailable;
}

/// <summary>
/// Native PAR2 verification: finds each file of a set (by name, or by its 16 KiB hash when the post was obfuscated and
/// gives it its real name back), then checks size, MD5 and every slice (CRC32, MD5 when needed) to count damaged blocks.
/// </summary>
public static class Par2Verifier
{
    private const int BufferSize = 1 << 20;

    /// <summary>Verifies all recoverable files of <paramref name="set"/> in <paramref name="folder"/>.</summary>
    /// <param name="candidates">Files that are not .par2 files; renamed entries are updated in place.</param>
    /// <param name="progress">0..1 over the bytes to read.</param>
    public static Par2VerifyResult Verify(
        string folder,
        Par2Set set,
        Par2Candidates candidates,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = set.RecoverableFiles.ToList();
        var located = new List<(Par2FileDescription File, string? Path, string? RenamedFrom)>();

        // Files that already have their real name are never taken for another file of the set.
        foreach (var file in set.Files.Values)
        {
            if (TargetPath(folder, file.Name) is { } existing && File.Exists(existing))
            {
                candidates.Claim(existing);
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            located.Add(Locate(folder, file, candidates));
        }

        // Still missing: an obfuscated file damaged in its first 16 KiB. Same size and at least half of its slices
        // matching (by position) is enough to know which file it is.
        for (var i = 0; i < located.Count; i++)
        {
            if (located[i].Path is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                located[i] = LocateBySlices(folder, located[i].File, set, candidates, cancellationToken);
            }
        }

        // Non-recovery files (rare) are only renamed, they cannot be checked block by block.
        foreach (var id in set.NonRecoveryFileIds)
        {
            if (set.Files.TryGetValue(id, out var extra))
            {
                Locate(folder, extra, candidates);
            }
        }

        var totalBytes = Math.Max(1, located.Where(l => l.Path is not null).Sum(l => Math.Min(new FileInfo(l.Path!).Length, l.File.Length)));
        long doneBytes = 0;
        var statuses = new List<Par2FileStatus>();

        foreach (var (file, path, renamedFrom) in located)
        {
            var slices = set.SliceCount(file);
            var target = path ?? TargetPath(folder, file.Name) ?? System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file.Name));
            if (path is null)
            {
                statuses.Add(new Par2FileStatus(file.Name, target, Par2FileState.Missing, slices, slices, null));
                continue;
            }

            var offset = doneBytes;
            var damaged = CheckFile(path, file, set, bytes => progress?.Report(Math.Min(1, (double)(offset + bytes) / totalBytes)), cancellationToken);
            doneBytes += Math.Min(new FileInfo(path).Length, file.Length);
            statuses.Add(new Par2FileStatus(file.Name, path, damaged is null ? Par2FileState.Ok : Par2FileState.Damaged, slices, damaged ?? 0, renamedFrom));
        }

        progress?.Report(1);
        return new Par2VerifyResult(set, statuses);
    }

    /// <summary>The safe full path for a name from a .par2 file, or null for names that try to leave the folder.</summary>
    public static string? TargetPath(string folder, string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not "." and not "..")
            .Select(SanitizeSegment)
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            return null;
        }

        var root = System.IO.Path.GetFullPath(folder);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine([root, .. parts]));
        var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = segment.Select(c => invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    /// <summary>Finds the file on disk; renames an obfuscated file whose first 16 KiB match.</summary>
    private static (Par2FileDescription, string?, string?) Locate(string folder, Par2FileDescription file, Par2Candidates candidates)
    {
        var target = TargetPath(folder, file.Name);
        if (target is null)
        {
            return (file, null, null);
        }

        if (File.Exists(target))
        {
            candidates.Claim(target);
            return (file, target, null);
        }

        // Same size and same first 16 KiB first; then same first 16 KiB only (an obfuscated file that is also damaged).
        var match = candidates.Unclaimed().FirstOrDefault(c => c.Length == file.Length && candidates.Hash16K(c.Path).AsSpan().SequenceEqual(file.Md5First16K))
            ?? candidates.Unclaimed().FirstOrDefault(c => c.Length >= Par2Reader.First16K && file.Length >= Par2Reader.First16K
                && candidates.Hash16K(c.Path).AsSpan().SequenceEqual(file.Md5First16K));

        if (match is null)
        {
            return (file, null, null);
        }

        return Rename(file, match.Path, target, candidates);
    }

    private static (Par2FileDescription, string?, string?) LocateBySlices(string folder, Par2FileDescription file, Par2Set set, Par2Candidates candidates, CancellationToken cancellationToken)
    {
        var target = TargetPath(folder, file.Name);
        var checksums = set.SliceChecksums.GetValueOrDefault(file.FileId);
        var sliceCount = set.SliceCount(file);
        if (target is null || checksums is null || checksums.Length < sliceCount || sliceCount == 0)
        {
            return (file, null, null);
        }

        foreach (var candidate in candidates.Unclaimed().Where(c => c.Length == file.Length))
        {
            var crcs = candidates.SliceCrcs(candidate.Path, set.SliceSize, cancellationToken);
            var matching = 0;
            for (var i = 0; i < sliceCount && i < crcs.Length; i++)
            {
                if (crcs[i] == checksums[i].Crc32)
                {
                    matching++;
                }
            }

            if (matching * 2 >= sliceCount)
            {
                return Rename(file, candidate.Path, target, candidates);
            }
        }

        return (file, null, null);
    }

    private static (Par2FileDescription, string?, string?) Rename(Par2FileDescription file, string from, string target, Par2Candidates candidates)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Move(from, target);
        candidates.Renamed(from, target);
        return (file, target, System.IO.Path.GetFileName(from));
    }

    /// <summary>CRC32 of every slice (the last one padded with zeros), for matching renamed files.</summary>
    internal static uint[] ComputeSliceCrcs(string path, long sliceSize, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var length = stream.Length;
        var crcs = new uint[(int)((length + sliceSize - 1) / sliceSize)];
        var buffer = new byte[BufferSize];
        long position = 0;
        var crc = Crc32.Initial;

        while (position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position));
            if (read <= 0)
            {
                break;
            }

            var chunk = buffer.AsSpan(0, read);
            while (chunk.Length > 0)
            {
                var inSlice = (int)Math.Min(chunk.Length, sliceSize - (position % sliceSize));
                crc = Crc32.Append(crc, chunk[..inSlice]);
                position += inSlice;
                chunk = chunk[inSlice..];
                if (position % sliceSize == 0)
                {
                    crcs[(position / sliceSize) - 1] = Crc32.Finish(crc);
                    crc = Crc32.Initial;
                }
            }
        }

        if (position % sliceSize != 0)
        {
            crcs[^1] = Crc32.Finish(AppendZeros(crc, sliceSize - (position % sliceSize)));
        }

        return crcs;
    }

    /// <summary>Null when the file is intact, otherwise the number of slices to rebuild.</summary>
    private static int? CheckFile(string path, Par2FileDescription file, Par2Set set, Action<long> progress, CancellationToken cancellationToken)
    {
        var sliceSize = set.SliceSize;
        var checksums = set.SliceChecksums.GetValueOrDefault(file.FileId);
        var sliceCount = set.SliceCount(file);
        var crcs = new uint[sliceCount];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var actualLength = stream.Length;
        var readable = Math.Min(actualLength, file.Length);

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[BufferSize];
        long position = 0;
        var crc = Crc32.Initial;

        while (position < readable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, readable - position));
            if (read <= 0)
            {
                break;
            }

            md5.AppendData(buffer, 0, read);

            // Feed the slice CRCs; a read can span several slices.
            var chunk = buffer.AsSpan(0, read);
            while (chunk.Length > 0)
            {
                var slice = (int)(position / sliceSize);
                var inSlice = (int)Math.Min(chunk.Length, sliceSize - (position % sliceSize));
                crc = Crc32.Append(crc, chunk[..inSlice]);
                position += inSlice;
                chunk = chunk[inSlice..];
                if (position % sliceSize == 0)
                {
                    crcs[slice] = Crc32.Finish(crc);
                    crc = Crc32.Initial;
                }
            }

            progress(position);
        }

        if (actualLength == file.Length && md5.GetHashAndReset().AsSpan().SequenceEqual(file.Md5))
        {
            return null;
        }

        if (checksums is null || checksums.Length < sliceCount)
        {
            // No slice checksums: par2 still knows the file is damaged, but not by how much. Assume all of it.
            return sliceCount;
        }

        // Last partial slice: padded with zeros to the slice size.
        if (position % sliceSize != 0)
        {
            var slice = (int)(position / sliceSize);
            crc = AppendZeros(crc, sliceSize - (position % sliceSize));
            crcs[slice] = Crc32.Finish(crc);
        }

        var fullSlices = (int)(readable / sliceSize) + (readable % sliceSize != 0 ? 1 : 0);
        var damaged = new bool[sliceCount];
        for (var i = 0; i < sliceCount; i++)
        {
            // Slices beyond the end of a short file are missing; a slice cut short by truncation is damaged.
            var cutShort = i == fullSlices - 1 && readable < file.Length && readable % sliceSize != 0;
            damaged[i] = i >= fullSlices || cutShort || crcs[i] != checksums[i].Crc32;
        }

        var count = damaged.Count(d => d);
        if (count == 0)
        {
            // CRC32 collisions are possible; check the slice MD5s before calling it an unexplained mismatch.
            count = CountByMd5(stream, file, sliceSize, sliceCount, checksums, cancellationToken);
        }

        return count;
    }

    private static int CountByMd5(FileStream stream, Par2FileDescription file, long sliceSize, int sliceCount, Par2SliceChecksum[] checksums, CancellationToken cancellationToken)
    {
        var damaged = 0;
        var buffer = new byte[sliceSize];
        stream.Position = 0;
        for (var i = 0; i < sliceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(buffer);
            var want = (int)Math.Min(sliceSize, file.Length - (i * sliceSize));
            var read = stream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
            if (read < want || !MD5.HashData(buffer).AsSpan().SequenceEqual(checksums[i].Md5))
            {
                damaged++;
            }
        }

        // Every slice is fine but the whole file is not (e.g. extra bytes at the end): par2 still has to rewrite it.
        return damaged;
    }

    private static uint AppendZeros(uint crc, long count)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (count > 0)
        {
            var n = (int)Math.Min(count, zeros.Length);
            crc = Crc32.Append(crc, zeros[..n]);
            count -= n;
        }

        return crc;
    }
}

/// <summary>The non-par2 files of a download folder, with cached 16 KiB hashes for deobfuscation.</summary>
public sealed class Par2Candidates
{
    private readonly Dictionary<string, byte[]> _hashes = new(StringComparer.Ordinal);
    private readonly List<Candidate> _files;
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint[]> _sliceCrcs = new(StringComparer.Ordinal);

    public Par2Candidates(IEnumerable<string> files)
    {
        _files = files.Select(f => new Candidate(f, new FileInfo(f).Length)).ToList();
    }

    public sealed record Candidate(string Path, long Length);

    public IReadOnlyList<Candidate> Files => _files;

    internal IEnumerable<Candidate> Unclaimed() => _files.Where(f => !_claimed.Contains(f.Path)).ToList();

    internal void Claim(string path) => _claimed.Add(path);

    internal byte[] Hash16K(string path)
    {
        if (_hashes.TryGetValue(path, out var hash))
        {
            return hash;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
        var buffer = new byte[Par2Reader.First16K];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        hash = MD5.HashData(buffer.AsSpan(0, read));
        _hashes[path] = hash;
        return hash;
    }

    internal uint[] SliceCrcs(string path, long sliceSize, CancellationToken cancellationToken)
    {
        var key = $"{sliceSize}|{path}";
        if (!_sliceCrcs.TryGetValue(key, out var crcs))
        {
            crcs = Par2Verifier.ComputeSliceCrcs(path, sliceSize, cancellationToken);
            _sliceCrcs[key] = crcs;
        }

        return crcs;
    }

    internal void Renamed(string from, string to)
    {
        var index = _files.FindIndex(f => f.Path == from);
        if (index >= 0)
        {
            _files[index] = _files[index] with { Path = to };
        }

        if (_hashes.Remove(from, out var hash))
        {
            _hashes[to] = hash;
        }

        _claimed.Add(to);
    }
}
