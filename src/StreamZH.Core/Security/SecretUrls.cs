// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;

namespace StreamZH.Core.Security;

/// <summary>
/// Removes secrets from URLs (and from text that contains URLs) before they are stored, shown or logged: the values of
/// well-known key parameters (Newznab "apikey" and "r", Jackett's "jackett_apikey", tracker "passkey", "token", magnet
/// "tr" announce URLs, …) and the password in "user:password@host". The rest stays readable, so a redacted release link
/// still identifies the release. Newznab's "t" (the API function, e.g. t=get) is not a secret and stays.
/// </summary>
public static partial class SecretUrls
{
    public const string Redacted = "(removed)";

    /// <summary>The query parameters whose values are treated as secrets (case-insensitive).</summary>
    public static IReadOnlyList<string> SecretParameters { get; } =
    [
        "apikey", "api_key", "apitoken", "api_token", "jackett_apikey", "r", "passkey", "pass_key", "token", "access_token",
        "authkey", "auth_key", "torrent_pass", "rsskey", "rss_key", "tr",
    ];

    /// <summary>The text with every secret parameter value and URL password replaced by <see cref="Redacted"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SecretParameterRegex().Replace(text, Redacted);
        return UserInfoPasswordRegex().Replace(result, Redacted);
    }

    /// <summary>Like <see cref="Redact(string?)"/>, but null stays null.</summary>
    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);

    /// <summary>True when <see cref="Redact(string?)"/> would change the text.</summary>
    public static bool ContainsSecret(string? text) => !string.IsNullOrEmpty(text) && Redact(text) != text;

    // The value of a secret parameter after ?, & or ; up to the next separator.
    [GeneratedRegex(
        @"(?<=[?&;](?:apikey|api_key|apitoken|api_token|jackett_apikey|r|passkey|pass_key|token|access_token|authkey|auth_key|torrent_pass|rsskey|rss_key|tr)=)(?!\(removed\))[^&#\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameterRegex();

    // scheme://user:password@host: the password.
    [GeneratedRegex(@"(?<=[a-z][a-z0-9+.-]*://[^/\s:@""'<>]*:)(?!\(removed\)@)[^/\s@""'<>]+(?=@)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPasswordRegex();
}
