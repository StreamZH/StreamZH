// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StreamZH.Library.PostProcessing;

/// <summary>A source file as a PAR2 File Description packet describes it.</summary>
/// <param name="FileId">Hex id that links the description to its slice checksums.</param>
/// <param name="Md5">MD5 of the whole file.</param>
/// <param name="Md5First16K">MD5 of the first 16 KiB, used to recognise renamed (obfuscated) files.</param>
/// <param name="Name">File name as stored in the set; may contain '/' for sub folders.</param>
public sealed record Par2FileDescription(string FileId, byte[] Md5, byte[] Md5First16K, long Length, string Name);

/// <summary>CRC32 and MD5 of one slice, computed over the slice padded with zeros to the slice size.</summary>
public readonly record struct Par2SliceChecksum(byte[] Md5, uint Crc32);

/// <summary>Everything known about one recovery set after reading its .par2 files.</summary>
public sealed class Par2Set(string setId)
{
    public string SetId { get; } = setId;

    /// <summary>0 until a Main packet was read; a set without it cannot be verified.</summary>
    public long SliceSize { get; internal set; }

    public IReadOnlyList<string> RecoveryFileIds { get; internal set; } = [];

    public IReadOnlyList<string> NonRecoveryFileIds { get; internal set; } = [];

    public Dictionary<string, Par2FileDescription> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Par2SliceChecksum[]> SliceChecksums { get; } = new(StringComparer.Ordinal);

    /// <summary>Exponents of recovery slices; with <c>verifyRecoveryData</c> only those whose packet hash matched.</summary>
    public HashSet<uint> RecoveryExponents { get; } = [];

    public string? Creator { get; internal set; }

    /// <summary>The .par2 files that contained packets of this set.</summary>
    public List<string> Par2Files { get; } = [];

    /// <summary>Unicode names (UniFileN packets) win over the plain names.</summary>
    internal Dictionary<string, string> UnicodeNames { get; } = new(StringComparer.Ordinal);

    public bool HasMain => SliceSize > 0;

    public int RecoveryBlocks => RecoveryExponents.Count;

    /// <summary>Files that can be verified and repaired, in Main packet order.</summary>
    public IEnumerable<Par2FileDescription> RecoverableFiles =>
        RecoveryFileIds.Select(id => Files.GetValueOrDefault(id)).OfType<Par2FileDescription>();

    public int SliceCount(Par2FileDescription file) => SliceSize <= 0 ? 0 : (int)((file.Length + SliceSize - 1) / SliceSize);
}

/// <summary>All recovery sets found in a folder (usually one per release).</summary>
public sealed class Par2Catalog
{
    public Dictionary<string, Par2Set> Sets { get; } = new(StringComparer.Ordinal);

    internal Par2Set GetOrAdd(string setId)
    {
        if (!Sets.TryGetValue(setId, out var set))
        {
            set = new Par2Set(setId);
            Sets[setId] = set;
        }

        return set;
    }
}

/// <summary>
/// Reads PAR 2.0 packets. Damaged packets (bad hash, impossible length) are skipped and the reader searches for the
/// next packet header, like par2cmdline does, so a partly downloaded .par2 still gives everything it can.
/// </summary>
public static class Par2Reader
{
    public const int HeaderSize = 64;
    public const int First16K = 16 * 1024;

    public static ReadOnlySpan<byte> Magic => "PAR2\0PKT"u8;

    internal static ReadOnlySpan<byte> MainType => "PAR 2.0\0Main\0\0\0\0"u8;
    internal static ReadOnlySpan<byte> FileDescType => "PAR 2.0\0FileDesc"u8;
    internal static ReadOnlySpan<byte> IfscType => "PAR 2.0\0IFSC\0\0\0\0"u8;
    internal static ReadOnlySpan<byte> RecoveryType => "PAR 2.0\0RecvSlic"u8;
    internal static ReadOnlySpan<byte> CreatorType => "PAR 2.0\0Creator\0"u8;
    internal static ReadOnlySpan<byte> UnicodeNameType => "PAR 2.0\0UniFileN"u8;

    // Anything but a recovery slice is small; a bigger claimed length is a damaged header.
    private const long MaxMetadataPacket = 64L * 1024 * 1024;

    /// <summary>True when the file starts with a PAR2 packet header, whatever its name (obfuscated posts rename .par2 files too).</summary>
    public static bool IsPar2File(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            Span<byte> head = stackalloc byte[8];
            return stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual(Magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Par2Catalog ReadFiles(IEnumerable<string> paths, bool verifyRecoveryData, CancellationToken cancellationToken = default)
    {
        var catalog = new Par2Catalog();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadInto(path, catalog, verifyRecoveryData, cancellationToken);
        }

        return catalog;
    }

    /// <param name="verifyRecoveryData">
    /// False: recovery slices are counted from their header only (fast, seeks over the data).
    /// True: the packet hash of every recovery slice is checked, so only usable blocks count.
    /// </param>
    public static void ReadInto(string path, Par2Catalog catalog, bool verifyRecoveryData, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        ReadInto(stream, path, catalog, verifyRecoveryData, cancellationToken);
    }

    public static void ReadInto(Stream stream, string sourceName, Par2Catalog catalog, bool verifyRecoveryData, CancellationToken cancellationToken = default)
    {
        var length = stream.Length;
        var header = new byte[HeaderSize];
        var exponent = new byte[4];
        long position = 0;

        while (position + HeaderSize <= length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;
            stream.ReadExactly(header);

            if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            {
                position = FindNextMagic(stream, position + 1, length);
                continue;
            }

            var packetLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            var type = header.AsSpan(48, 16);
            var isRecovery = type.SequenceEqual(RecoveryType);

            if (packetLength < HeaderSize || packetLength % 4 != 0 || packetLength > (ulong)(length - position)
                || (!isRecovery && (long)packetLength > MaxMetadataPacket))
            {
                position = FindNextMagic(stream, position + 1, length);
                continue;
            }

            var bodyLength = (long)packetLength - HeaderSize;
            var setId = Convert.ToHexStringLower(header.AsSpan(32, 16));

            if (isRecovery && !verifyRecoveryData)
            {
                if (bodyLength >= 4)
                {
                    stream.ReadExactly(exponent);
                    var set = catalog.GetOrAdd(setId);
                    set.RecoveryExponents.Add(BinaryPrimitives.ReadUInt32LittleEndian(exponent));
                    AddSource(set, sourceName);
                }

                position += (long)packetLength;
                continue;
            }

            if (!TryReadBody(stream, header, bodyLength, keepBody: !isRecovery, out var body, out var firstWord))
            {
                position = FindNextMagic(stream, position + 1, length);
                continue;
            }

            var target = catalog.GetOrAdd(setId);
            AddSource(target, sourceName);
            if (isRecovery)
            {
                target.RecoveryExponents.Add(firstWord);
            }
            else
            {
                Parse(target, type, body!);
            }

            position += (long)packetLength;
        }
    }

    /// <summary>Reads the packet body and checks the packet MD5 (covers set id, type and body).</summary>
    private static bool TryReadBody(Stream stream, byte[] header, long bodyLength, bool keepBody, out byte[]? body, out uint firstWord)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header, 32, 32);
        firstWord = 0;
        body = null;

        if (keepBody)
        {
            body = new byte[bodyLength];
            stream.ReadExactly(body);
            md5.AppendData(body);
        }
        else
        {
            var buffer = new byte[(int)Math.Min(bodyLength, 1 << 20)];
            var remaining = bodyLength;
            var first = true;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    return false;
                }

                if (first && read >= 4)
                {
                    firstWord = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                }

                first = false;
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }

        Span<byte> hash = stackalloc byte[16];
        md5.GetHashAndReset(hash);
        return hash.SequenceEqual(header.AsSpan(16, 16));
    }

    private static void Parse(Par2Set set, ReadOnlySpan<byte> type, byte[] body)
    {
        if (type.SequenceEqual(MainType))
        {
            if (body.Length < 12 || set.HasMain)
            {
                return;
            }

            var sliceSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body);
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)), (uint)((body.Length - 12) / 16));
            var ids = new List<string>();
            for (var offset = 12; offset + 16 <= body.Length; offset += 16)
            {
                ids.Add(Convert.ToHexStringLower(body.AsSpan(offset, 16)));
            }

            set.SliceSize = sliceSize;
            set.RecoveryFileIds = ids[..count];
            set.NonRecoveryFileIds = ids[count..];
        }
        else if (type.SequenceEqual(FileDescType))
        {
            if (body.Length < 56)
            {
                return;
            }

            var fileId = Convert.ToHexStringLower(body.AsSpan(0, 16));
            var name = DecodeName(body.AsSpan(56));
            if (set.UnicodeNames.TryGetValue(fileId, out var unicode))
            {
                name = unicode;
            }

            set.Files[fileId] = new Par2FileDescription(
                fileId,
                body.AsSpan(16, 16).ToArray(),
                body.AsSpan(32, 16).ToArray(),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(48)),
                name);
        }
        else if (type.SequenceEqual(IfscType))
        {
            if (body.Length < 16)
            {
                return;
            }

            var fileId = Convert.ToHexStringLower(body.AsSpan(0, 16));
            var count = (body.Length - 16) / 20;
            var slices = new Par2SliceChecksum[count];
            for (var i = 0; i < count; i++)
            {
                var offset = 16 + (i * 20);
                slices[i] = new Par2SliceChecksum(body.AsSpan(offset, 16).ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 16)));
            }

            set.SliceChecksums[fileId] = slices;
        }
        else if (type.SequenceEqual(CreatorType))
        {
            set.Creator ??= Encoding.ASCII.GetString(body).TrimEnd('\0');
        }
        else if (type.SequenceEqual(UnicodeNameType))
        {
            if (body.Length < 18)
            {
                return;
            }

            var fileId = Convert.ToHexStringLower(body.AsSpan(0, 16));
            var name = Encoding.Unicode.GetString(body.AsSpan(16, (body.Length - 16) & ~1)).TrimEnd('\0');
            if (name.Length == 0)
            {
                return;
            }

            set.UnicodeNames[fileId] = name;
            if (set.Files.TryGetValue(fileId, out var existing))
            {
                set.Files[fileId] = existing with { Name = name };
            }
        }
    }

    /// <summary>Names are ASCII by the spec but UTF-8 in practice; anything that is not valid UTF-8 is read as Latin-1.</summary>
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        if (end >= 0)
        {
            raw = raw[..end];
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    private static void AddSource(Par2Set set, string sourceName)
    {
        if (!set.Par2Files.Contains(sourceName))
        {
            set.Par2Files.Add(sourceName);
        }
    }

    /// <summary>Position of the next "PAR2\0PKT" at or after <paramref name="from"/>, or <paramref name="length"/> when there is none.</summary>
    private static long FindNextMagic(Stream stream, long from, long length)
    {
        var magic = Magic;
        var buffer = new byte[1 << 16];
        var position = from;

        while (position + magic.Length <= length)
        {
            stream.Position = position;
            var read = stream.ReadAtLeast(buffer, (int)Math.Min(buffer.Length, length - position), throwOnEndOfStream: false);
            if (read < magic.Length)
            {
                break;
            }

            var index = buffer.AsSpan(0, read).IndexOf(magic);
            if (index >= 0)
            {
                return position + index;
            }

            // Keep the last bytes: the magic may cross the buffer boundary.
            position += read - (magic.Length - 1);
        }

        return length;
    }
}
