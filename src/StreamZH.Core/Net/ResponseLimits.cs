// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net.Http;
using System.Text;

namespace StreamZH.Core.Net;

/// <summary>
/// Upper limits for answers from servers an admin can point StreamZH at (import lists, connections, migration
/// sources, TRaSH Guides), so a wrong or hostile server cannot fill the memory or the disk.
/// </summary>
public static class ResponseLimits
{
    public const long MB = 1024 * 1024;

    /// <summary>JSON/XML answers of import lists, connections and the Prowlarr import.</summary>
    public const long Json = 20 * MB;

    /// <summary>Radarr/Sonarr migration: the whole library comes in one answer.</summary>
    public const long ArrMigration = 256 * MB;

    /// <summary>SABnzbd migration (config and history).</summary>
    public const long SabnzbdMigration = 64 * MB;

    /// <summary>The TRaSH Guides archive (tens of MB today).</summary>
    public const long TrashArchive = 256 * MB;

    /// <summary>One file from the TRaSH Guides archive (JSON, a few KB each).</summary>
    public const long TrashFile = 16 * MB;

    /// <summary>Copies at most <paramref name="max"/> bytes; more throws <see cref="InvalidDataException"/>.</summary>
    public static async Task CopyAsync(Stream source, Stream destination, long max, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw TooLarge(what, max);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous <see cref="CopyAsync"/>.</summary>
    public static void Copy(Stream source, Stream destination, long max, string what)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw TooLarge(what, max);
            }

            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>The body as text (charset from the answer, UTF-8 otherwise), at most <paramref name="max"/> bytes.</summary>
    public static async Task<string> ReadStringAsync(HttpContent content, long max, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is { } length && length > max)
        {
            throw TooLarge(what, max);
        }

        using var buffer = new MemoryStream();
        await using (var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await CopyAsync(stream, buffer, max, what, cancellationToken).ConfigureAwait(false);
        }

        Encoding encoding;
        try
        {
            encoding = content.Headers.ContentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var preamble = encoding.Preamble;
        if (preamble.Length > 0 && bytes.StartsWith(preamble))
        {
            bytes = bytes[preamble.Length..];
        }

        return encoding.GetString(bytes);
    }

    public static InvalidDataException TooLarge(string what, long max) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{what} is larger than {max / MB} MB."));
}
