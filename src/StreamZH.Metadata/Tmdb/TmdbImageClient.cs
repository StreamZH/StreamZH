// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;

namespace StreamZH.Metadata.Tmdb;

/// <summary>Downloads posters, backdrops and stills from the TMDB image CDN (no API key needed).</summary>
public interface ITmdbImageClient
{
    /// <summary>Writes the image to <paramref name="destination"/>.</summary>
    /// <exception cref="TmdbException">Invalid path, not found or the CDN is not reachable.</exception>
    Task DownloadAsync(string path, string size, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class TmdbImageClient(HttpClient http) : ITmdbImageClient
{
    /// <summary>A poster is far smaller; this only protects the disk from a misbehaving server.</summary>
    public const long MaxImageBytes = 50L * 1024 * 1024;

    public async Task DownloadAsync(string path, string size, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!TmdbImages.IsValidPath(path) || !TmdbImages.Sizes.Contains(size))
        {
            throw new TmdbException($"Not a TMDB image: {size}{path}", 400);
        }

        var uri = new Uri(http.BaseAddress ?? TmdbImages.BaseAddress, $"{size}{path}");
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbException($"TMDB images are not reachable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new TmdbException("Image not found on TMDB.", 404);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TmdbException($"TMDB images answered {(int)response.StatusCode}.", (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                throw new TmdbException("The image is too large.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxImageBytes)
                {
                    throw new TmdbException("The image is too large.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }
}
