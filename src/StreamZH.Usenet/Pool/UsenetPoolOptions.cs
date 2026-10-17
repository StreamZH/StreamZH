// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Security;
using StreamZH.Core.Downloads;
using StreamZH.Usenet.Nntp;

namespace StreamZH.Usenet.Pool;

/// <summary>Timings of the connection pool. The defaults are what the app uses; tests shorten them.</summary>
public sealed class UsenetPoolOptions
{
    /// <summary>Idle connections are closed after this time (servers drop them anyway after a few minutes).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A server that refused the login or could not be reached at all is skipped for this long.</summary>
    public TimeSpan FailureBackOff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Back-off of an optional server (<see cref="ServerFailureAction.IgnoreLonger"/>).</summary>
    public TimeSpan OptionalFailureBackOff { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Tries per server and article on transient errors (timeout, dropped connection), each on another connection.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The "tries per article" setting; when set and positive it wins over <see cref="MaxAttempts"/>. Read on every article.</summary>
    public Func<int>? AttemptsSetting { get; init; }

    /// <summary>Upper bound of the connect timeout (each server's own timeout can make it shorter).</summary>
    public TimeSpan ConnectTimeout { get; init; } = NntpConnectionSettings.DefaultConnectTimeout;

    /// <summary>Null (the app): each server's own timeout. Tests set it to override every server.</summary>
    public TimeSpan? ReadTimeout { get; init; }

    /// <summary>The download speed is the average over this window.</summary>
    public TimeSpan SpeedWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Only for tests with a self-signed certificate; null in the app (each server's certificate check applies).</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>Tries per server and article right now.</summary>
    public int Attempts => AttemptsSetting?.Invoke() is > 0 and var attempts ? attempts : Math.Max(1, MaxAttempts);

    public TimeSpan BackOffFor(UsenetServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.OnFailure == ServerFailureAction.IgnoreLonger ? OptionalFailureBackOff : FailureBackOff;
    }

    public NntpConnectionSettings SettingsFor(UsenetServer server) =>
        NntpConnectionSettings.For(server, ConnectTimeout, ReadTimeout, CertificateValidation);
}
