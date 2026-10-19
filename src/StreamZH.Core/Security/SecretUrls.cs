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

    /// <summary>
    /// 0.12 (SV2 N-04): a URL in a setting (webhook, notification server, import list) with its credentials replaced by
    /// <paramref name="mask"/>: the user info ("user:password@" or "token@") and the values of key, token and password
    /// parameters (a wider list than <see cref="SecretParameters"/>: also key, secret, password, pass, pwd, sig, auth, code).
    /// Text without credentials comes back unchanged.
    /// </summary>
    public static string MaskCredentials(string? text, string mask)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SettingParameterRegex().Replace(text, m => m.Value == mask ? m.Value : mask);
        return UserInfoRegex().Replace(result, m => m.Groups["info"].Value == mask ? m.Value : m.Groups["scheme"].Value + mask + "@");
    }

    /// <summary>True when <paramref name="text"/> carries credentials that <see cref="MaskCredentials"/> hides.</summary>
    public static bool HasCredentials(string? text) => !string.IsNullOrEmpty(text) && MaskCredentials(text, "\u0001") != text;

    [GeneratedRegex(
        @"(?<=[?&;#](?:apikey|api_key|apitoken|api_token|jackett_apikey|r|passkey|pass_key|token|access_token|authkey|auth_key|torrent_pass|rsskey|rss_key|tr|key|secret|client_secret|password|passwd|pass|pwd|sig|signature|auth|code)=)[^&#\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SettingParameterRegex();

    // scheme://userinfo@host: the whole user info (a user name alone is often a token).
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<info>[^/\s@""'<>?#]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoRegex();

    // The value of a secret parameter after ?, & or ; up to the next separator.
    [GeneratedRegex(
        @"(?<=[?&;](?:apikey|api_key|apitoken|api_token|jackett_apikey|r|passkey|pass_key|token|access_token|authkey|auth_key|torrent_pass|rsskey|rss_key|tr)=)(?!\(removed\))[^&#\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretParameterRegex();

    // scheme://user:password@host: the password.
    [GeneratedRegex(@"(?<=[a-z][a-z0-9+.-]*://[^/\s:@""'<>]*:)(?!\(removed\)@)[^/\s@""'<>]+(?=@)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPasswordRegex();
}
