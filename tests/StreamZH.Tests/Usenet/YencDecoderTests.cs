// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Hashing;
using System.Text;
using StreamZH.Usenet.Yenc;

namespace StreamZH.Tests.Usenet;

public class YencDecoderTests
{
    [Fact]
    public void Decodes_single_part_and_validates_crc()
    {
        var payload = AllByteValues(3);
        var article = Encode(payload, "test file.bin", part: null);

        var result = YencDecoder.Decode(article);

        Assert.Equal(payload, result.Data);
        Assert.Equal("test file.bin", result.Name);
        Assert.Equal(0, result.Offset);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Decodes_multi_part_with_offset()
    {
        var payload = AllByteValues(2);
        var article = Encode(payload, "movie.part01.rar", part: (Number: 2, Total: 5, Begin: 384001));

        var result = YencDecoder.Decode(article);

        Assert.Equal(2, result.Part);
        Assert.Equal(5, result.Total);
        Assert.Equal(384000, result.Offset);
        Assert.Equal(payload, result.Data);
        Assert.True(result.CrcMatches);
    }

    [Fact]
    public void Detects_corrupted_data()
    {
        var article = Encode(AllByteValues(1), "x.bin", part: null);
        var lastDataLine = Array.LastIndexOf(article, (byte)'\n', Array.LastIndexOf(article, (byte)'=') - 2) - 1;
        article[lastDataLine] ^= 0x01;

        var result = YencDecoder.Decode(article);

        Assert.False(result.CrcMatches);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Throws_on_truncated_article()
    {
        var article = Encode(AllByteValues(1), "x.bin", part: null);
        var truncated = article.AsSpan(0, article.Length / 2).ToArray();

        Assert.Throws<YencException>(() => YencDecoder.Decode(truncated));
    }

    private static byte[] AllByteValues(int repeat) =>
        Enumerable.Repeat(Enumerable.Range(0, 256).Select(i => (byte)i), repeat).SelectMany(x => x).ToArray();

    /// <summary>Reference encoder following the yEnc 1.3 spec (critical chars: NUL, LF, CR, '=', plus leading '.', TAB, space).</summary>
    private static byte[] Encode(byte[] data, string name, (int Number, int Total, long Begin)? part)
    {
        var buffer = new MemoryStream();
        void Write(string s) => buffer.Write(Encoding.Latin1.GetBytes(s));

        var crc = Crc32.HashToUInt32(data).ToString("x8");
        if (part is { } p)
        {
            Write($"=ybegin part={p.Number} total={p.Total} line=128 size={p.Begin + data.Length + 1000} name={name}\r\n");
            Write($"=ypart begin={p.Begin} end={p.Begin + data.Length - 1}\r\n");
        }
        else
        {
            Write($"=ybegin line=128 size={data.Length} name={name}\r\n");
        }

        var column = 0;
        foreach (var b in data)
        {
            var c = (byte)(b + 42);
            var critical = c is 0 or 10 or 13 or 61 || (column == 0 && c is 46 or 9 or 32);
            if (critical)
            {
                buffer.WriteByte((byte)'=');
                c = (byte)(c + 64);
                column++;
            }

            buffer.WriteByte(c);
            if (++column >= 128)
            {
                Write("\r\n");
                column = 0;
            }
        }

        if (column > 0)
        {
            Write("\r\n");
        }

        Write(part is { } q
            ? $"=yend size={data.Length} part={q.Number} pcrc32={crc}\r\n"
            : $"=yend size={data.Length} crc32={crc}\r\n");

        return buffer.ToArray();
    }
}