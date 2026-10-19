// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Security;

/// <summary>
/// 0.12: a stored secret (password, API key, token) is only reused for the address it was entered for. The API shows
/// stored secrets as "********"; sending that back keeps the stored value, but only when the destination (URL, host,
/// port, base URL, API path, …) is unchanged. Otherwise the secret must be typed again, so nobody who can edit settings
/// can send a stored secret to an address of their choice.
/// </summary>
public static class SecretReuse
{
    /// <summary>"Enter the password again to test a changed address."</summary>
    /// <param name="label">What the secret is called in the form: "Password", "API key", "Auth token".</param>
    /// <param name="test">A connection test; otherwise saving.</param>
    public static string ChangedAddressMessage(string label, bool test) =>
        $"Enter the {Lower(label)} again to {(test ? "test" : "save")} a changed address.";

    /// <summary>Both texts the same, ignoring surrounding white space; null and empty are equal.</summary>
    public static bool Same(string? a, string? b) => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.Ordinal);

    /// <summary>Host names compare without case (DNS is case-insensitive); everything else exactly.</summary>
    public static bool SameHost(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim().TrimEnd('.'), (b ?? string.Empty).Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    // "Password" → "password", "API key" stays.
    private static string Lower(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && char.IsLower(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
}
