// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Torrent;

/// <summary>
/// Announce URLs of private trackers carry the user's passkey in the path ("/abc123/announce") or the query
/// ("?passkey=…"). Anything shown or logged keeps only the scheme, host and port.
/// </summary>
public static class TrackerUrls
{
    /// <summary>"https://tracker.example.org:443/…" style text without path, query or credentials.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "tracker";
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}";
    }

    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Redact(uri) : "tracker";

    /// <summary>Removes every occurrence of the tracker URLs (and their path/query parts) from a message.</summary>
    public static string Scrub(string message, IEnumerable<Uri> trackers)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(trackers);
        foreach (var tracker in trackers)
        {
            var original = tracker.OriginalString;
            if (original.Length > 0)
            {
                message = message.Replace(original, Redact(tracker), StringComparison.OrdinalIgnoreCase);
            }

            var secret = tracker.PathAndQuery;
            if (secret.Length > 1 && secret != "/announce")
            {
                message = message.Replace(secret, "/…", StringComparison.OrdinalIgnoreCase);
            }
        }

        return message;
    }
}
