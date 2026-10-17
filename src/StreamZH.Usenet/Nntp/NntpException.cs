// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Usenet.Nntp;

/// <summary>Why an NNTP command or connection failed.</summary>
public enum NntpErrorKind
{
    /// <summary>TCP connect failed or timed out (host down, wrong port, DNS).</summary>
    Connect,

    /// <summary>TLS handshake failed (certificate not trusted, no TLS on this port).</summary>
    Tls,

    /// <summary>The server did not answer within the read timeout.</summary>
    Timeout,

    /// <summary>The server closed the connection.</summary>
    Closed,

    /// <summary>An answer that does not follow the protocol, or an unexpected status code.</summary>
    Protocol,

    /// <summary>Temporarily refused, e.g. too many connections for the account.</summary>
    Busy,

    /// <summary>User name or password rejected, or the server wants a login.</summary>
    Auth,

    /// <summary>The server refused the connection for good (e.g. "access denied").</summary>
    Rejected,
}

/// <summary>An NNTP connection or command failed. Messages never contain the password.</summary>
public class NntpException(string message, NntpErrorKind kind, int? code = null, Exception? inner = null) : Exception(message, inner)
{
    public NntpErrorKind Kind { get; } = kind;

    /// <summary>The NNTP status code of the answer, when there was one.</summary>
    public int? Code { get; } = code;
}

/// <summary>430: the server does not have the article. The connection stays usable.</summary>
public sealed class NntpArticleNotFoundException(string messageId, string serverMessage)
    : NntpException($"No such article <{messageId}>: {serverMessage}", NntpErrorKind.Protocol, 430)
{
    public string MessageId { get; } = messageId;
}
