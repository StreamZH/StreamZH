// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Security;
using StreamZH.Core.Downloads;

namespace StreamZH.Usenet.Nntp;

/// <summary>
/// Where and how to connect. A class rather than a record so the password never ends up in a generated ToString().
/// </summary>
public sealed class NntpConnectionSettings
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(60);

    public required string Host { get; init; }

    public required int Port { get; init; }

    public bool UseSsl { get; init; }

    /// <summary>Empty: no AUTHINFO.</summary>
    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>TCP connect plus TLS handshake.</summary>
    public TimeSpan ConnectTimeout { get; init; } = DefaultConnectTimeout;

    /// <summary>Longest wait for data while the server is expected to answer.</summary>
    public TimeSpan ReadTimeout { get; init; } = DefaultReadTimeout;

    /// <summary>
    /// Null means the operating system's validation: the certificate must be trusted, valid and match <see cref="Host"/>
    /// (<see cref="CertificateCheck.Strict"/>). <see cref="For"/> sets it for the weaker checks of a server; tests also
    /// use it to trust their self-signed certificate.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>
    /// Settings for a server: its timeout (read timeout; the connect timeout is at most <paramref name="connectTimeout"/>)
    /// and its certificate check. <paramref name="readTimeout"/> and <paramref name="certificateValidation"/> override the
    /// server's settings (tests).
    /// </summary>
    public static NntpConnectionSettings For(UsenetServer server, TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null, RemoteCertificateValidationCallback? certificateValidation = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var timeout = server.Timeout;
        var connect = connectTimeout ?? DefaultConnectTimeout;
        return new NntpConnectionSettings
        {
            Host = server.Host,
            Port = server.Port,
            UseSsl = server.UseSsl,
            Username = server.Username,
            Password = server.Password,
            ConnectTimeout = connect < timeout ? connect : timeout,
            ReadTimeout = readTimeout ?? timeout,
            CertificateValidation = certificateValidation ?? ValidationFor(server.CertificateCheck),
        };
    }

    /// <summary>Null for <see cref="CertificateCheck.Strict"/> (the operating system decides).</summary>
    public static RemoteCertificateValidationCallback? ValidationFor(CertificateCheck check) => check switch
    {
        // A certificate for this host name is required; an untrusted, self-signed or expired chain is accepted.
        CertificateCheck.Minimal => static (_, certificate, _, errors) =>
            certificate is not null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None,
        CertificateCheck.Off => static (_, _, _, _) => true,
        _ => null,
    };

    public override string ToString() => $"{Host}:{Port}{(UseSsl ? " (SSL)" : string.Empty)}";
}
