// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Library.PostProcessing;

/// <summary>CRC-32 (IEEE 802.3, the one PAR2 and zip use), slicing-by-8 so slice checks keep up with disk reads.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Start value for <see cref="Append"/>.</summary>
    public const uint Initial = 0xFFFFFFFF;

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Initial, data));

    /// <summary>Continues a running CRC; call <see cref="Finish"/> at the end.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Table;
        var i = 0;

        while (data.Length - i >= 8)
        {
            var one = crc ^ (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            var two = (uint)(data[i + 4] | data[i + 5] << 8 | data[i + 6] << 16 | data[i + 7] << 24);
            crc = table[(7 * 256) + (one & 0xFF)]
                ^ table[(6 * 256) + ((one >> 8) & 0xFF)]
                ^ table[(5 * 256) + ((one >> 16) & 0xFF)]
                ^ table[(4 * 256) + (one >> 24)]
                ^ table[(3 * 256) + (two & 0xFF)]
                ^ table[(2 * 256) + ((two >> 8) & 0xFF)]
                ^ table[256 + ((two >> 16) & 0xFF)]
                ^ table[two >> 24];
            i += 8;
        }

        for (; i < data.Length; i++)
        {
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Finish(uint crc) => ~crc;

    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        for (var n = 0; n < 256; n++)
        {
            for (var s = 1; s < 8; s++)
            {
                var previous = table[((s - 1) * 256) + n];
                table[(s * 256) + n] = table[previous & 0xFF] ^ (previous >> 8);
            }
        }

        return table;
    }
}
