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
    /// Only for tests with a self-signed certificate. Null (always in the app) means the operating system's validation:
    /// the certificate must be trusted and match <see cref="Host"/>. There is deliberately no "ignore certificate errors" setting.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    public static NntpConnectionSettings For(UsenetServer server, TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null, RemoteCertificateValidationCallback? certificateValidation = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new NntpConnectionSettings
        {
            Host = server.Host,
            Port = server.Port,
            UseSsl = server.UseSsl,
            Username = server.Username,
            Password = server.Password,
            ConnectTimeout = connectTimeout ?? DefaultConnectTimeout,
            ReadTimeout = readTimeout ?? DefaultReadTimeout,
            CertificateValidation = certificateValidation,
        };
    }

    public override string ToString() => $"{Host}:{Port}{(UseSsl ? " (SSL)" : string.Empty)}";
}
