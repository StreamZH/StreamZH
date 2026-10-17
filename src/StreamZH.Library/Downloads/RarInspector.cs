// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers.Binary;

namespace StreamZH.Library.Downloads;

/// <summary>
/// Tells from the start of a RAR volume (the first article is enough) whether it is password protected, like SABnzbd's
/// "Action when encrypted RAR is downloaded". Every volume carries the archive header and the header of the first file,
/// so the first article of any volume will do. Only reads headers; CRCs are not checked.
/// </summary>
public static class RarInspector
{
    private static ReadOnlySpan<byte> Rar4Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    private static ReadOnlySpan<byte> Rar5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    public static bool IsRar(ReadOnlySpan<byte> data) => data.StartsWith(Rar4Signature) || data.StartsWith(Rar5Signature);

    /// <summary>True: encrypted (headers or the first file). False: not encrypted. Null: not a RAR volume or not readable.</summary>
    public static bool? IsEncrypted(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(Rar5Signature))
        {
            return Rar5(data[Rar5Signature.Length..]);
        }

        if (data.StartsWith(Rar4Signature))
        {
            return Rar4(data[Rar4Signature.Length..]);
        }

        return null;
    }

    // RAR 4: blocks of CRC(2) TYPE(1) FLAGS(2) SIZE(2) [ADD_SIZE(4)]. MAIN_HEAD 0x73 flag 0x0080: headers encrypted;
    // FILE_HEAD 0x74 flag 0x0004: the file is encrypted.
    private static bool? Rar4(ReadOnlySpan<byte> data)
    {
        var position = 0;
        while (position + 7 <= data.Length)
        {
            var type = data[position + 2];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 3)..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 5)..]);
            if (size < 7)
            {
                return null;
            }

            switch (type)
            {
                case 0x73 when (flags & 0x0080) != 0:
                    return true;
                case 0x74:
                    return (flags & 0x0004) != 0;
                case 0x7B: // end of archive
                    return false;
            }

            long next = position + size;
            if ((flags & 0x8000) != 0 && position + 11 <= data.Length)
            {
                next += BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 7)..]);
            }

            if (next <= position || next > int.MaxValue)
            {
                return null;
            }

            position = (int)next;
        }

        return null;
    }

    // RAR 5: blocks of CRC32(4) SIZE(vint) then SIZE bytes: TYPE(vint) FLAGS(vint) [EXTRA_SIZE(vint) if 1] [DATA_SIZE(vint) if 2] ….
    // Type 4 = archive encryption header (headers encrypted). Type 2 = file header; an extra record of type 1 = file encryption.
    private static bool? Rar5(ReadOnlySpan<byte> data)
    {
        var position = 0;
        while (position + 4 < data.Length)
        {
            var cursor = position + 4;
            if (!ReadVint(data, ref cursor, out var headerSize) || headerSize == 0)
            {
                return null;
            }

            var headerStart = cursor;
            var headerEnd = headerStart + (long)headerSize;
            if (!ReadVint(data, ref cursor, out var type) || !ReadVint(data, ref cursor, out var flags))
            {
                return null;
            }

            ulong extraSize = 0;
            ulong dataSize = 0;
            if ((flags & 1) != 0 && !ReadVint(data, ref cursor, out extraSize))
            {
                return null;
            }

            if ((flags & 2) != 0 && !ReadVint(data, ref cursor, out dataSize))
            {
                return null;
            }

            switch (type)
            {
                case 4:
                    return true;
                case 2:
                    if (headerEnd > data.Length)
                    {
                        return null;
                    }

                    return FileEncrypted(data[(int)(headerEnd - (long)extraSize)..(int)headerEnd]);
                case 5: // end of archive
                    return false;
            }

            var next = headerEnd + (long)dataSize;
            if (next <= position || next > int.MaxValue)
            {
                return null;
            }

            position = (int)next;
        }

        return null;
    }

    private static bool? FileEncrypted(ReadOnlySpan<byte> extra)
    {
        var cursor = 0;
        while (cursor < extra.Length)
        {
            if (!ReadVint(extra, ref cursor, out var size) || size == 0)
            {
                return null;
            }

            var end = cursor + (long)size;
            if (!ReadVint(extra, ref cursor, out var recordType))
            {
                return null;
            }

            if (recordType == 1)
            {
                return true;
            }

            if (end > extra.Length)
            {
                return false;
            }

            cursor = (int)end;
        }

        return false;
    }

    private static bool ReadVint(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && position < data.Length; shift += 7)
        {
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
