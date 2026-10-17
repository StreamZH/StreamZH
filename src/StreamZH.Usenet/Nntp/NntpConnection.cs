// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using StreamZH.Core.Downloads;
using StreamZH.Usenet.Yenc;

namespace StreamZH.Usenet.Nntp;

/// <summary>A status line: three-digit code and the text after it.</summary>
public readonly record struct NntpResponse(int Code, string Text)
{
    public override string ToString() => $"{Code} {Text}".TrimEnd();
}

/// <summary>
/// One NNTP connection (RFC 3977, AUTHINFO from RFC 4643): TCP, optionally TLS, greeting, login, BODY by message-id, DATE, QUIT.
/// Reads through one pooled buffer; multi-line answers are dot-unstuffed and end at the "." line.
/// Not thread-safe: one command at a time, except for pipelined BODY commands (<see cref="SendBodyAsync"/> and
/// <see cref="ReceiveBodyAsync"/>: one sender and one reader at once, answers read in sending order). After a timeout, a lost
/// connection or a cancelled command the connection is no longer <see cref="IsUsable"/> and must be disposed.
/// </summary>
public sealed class NntpConnection : IAsyncDisposable
{
    /// <summary>Articles are usually below 1 MB; anything larger than this is treated as a broken answer.</summary>
    public const int MaxArticleBytes = 32 * 1024 * 1024;

    private const int ReadBufferSize = 64 * 1024;
    private const int ArticleBufferSize = 1024 * 1024;
    private const int MaxStatusLineLength = 8 * 1024;
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(2);

    private readonly Stream _stream;
    private readonly NntpConnectionSettings _settings;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
    private int _start;
    private int _end;
    private volatile bool _broken;
    private int _disposed;

    private NntpConnection(Stream stream, NntpConnectionSettings settings, string? tlsProtocol)
    {
        _stream = stream;
        _settings = settings;
        TlsProtocol = tlsProtocol;
    }

    /// <summary>Text of the greeting (200/201).</summary>
    public string Greeting { get; private set; } = string.Empty;

    /// <summary>"TLS 1.3" and similar; null without SSL.</summary>
    public string? TlsProtocol { get; }

    /// <summary>NNTP bytes read from the server so far (after TLS decryption).</summary>
    public long BytesReceived { get; private set; }

    /// <summary>False after a timeout, a lost connection, a cancelled command, QUIT or dispose.</summary>
    public bool IsUsable => !_broken && Volatile.Read(ref _disposed) == 0;

    private string Endpoint => $"{_settings.Host}:{_settings.Port}";

    /// <summary>Connects, reads the greeting and logs in when a user name is set.</summary>
    /// <exception cref="NntpException">Connect, TLS, greeting or login failed.</exception>
    public static async Task<NntpConnection> ConnectAsync(NntpConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var endpoint = $"{settings.Host}:{settings.Port}";
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        NntpConnection? connection = null;
        try
        {
            string? tls = null;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(settings.ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(settings.Host, settings.Port, connectTimeout.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new NntpException($"{endpoint} did not answer within {settings.ConnectTimeout.TotalSeconds:0} seconds.", NntpErrorKind.Connect, inner: ex);
                }
                catch (SocketException ex)
                {
                    throw new NntpException($"Could not connect to {endpoint}: {ex.Message}", NntpErrorKind.Connect, inner: ex);
                }

                stream = new NetworkStream(socket, ownsSocket: true);
                if (settings.UseSsl)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    stream = ssl;
                    await HandshakeTlsAsync(ssl, settings, endpoint, connectTimeout.Token, cancellationToken);
                    tls = TlsName(ssl.SslProtocol);
                }
            }

            connection = new NntpConnection(stream, settings, tls);
            await connection.GreetAndLoginAsync(cancellationToken);
            return connection;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
            else if (stream is not null)
            {
                await stream.DisposeAsync();
            }
            else
            {
                socket.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// BODY &lt;message-id&gt;, decoded with <see cref="YencDecoder"/>. A CRC or size mismatch still returns the data, with
    /// <see cref="DecodedArticle.CrcOk"/> false.
    /// </summary>
    /// <exception cref="NntpArticleNotFoundException">430: the server does not have it (connection stays usable).</exception>
    /// <exception cref="YencException">The body is not a complete yEnc part (connection stays usable).</exception>
    /// <exception cref="NntpException">Any other failure.</exception>
    public async Task<DecodedArticle> BodyAsync(string messageId, CancellationToken cancellationToken)
    {
        await SendBodyAsync(messageId, cancellationToken);
        return await ReceiveBodyAsync(messageId, cancellationToken);
    }

    /// <summary>
    /// Pipelining, first half: sends BODY without waiting for the answer. Answers come in the order the commands were sent;
    /// read each with <see cref="ReceiveBodyAsync"/>. One sender and one reader may run at the same time, not more.
    /// </summary>
    /// <exception cref="NntpException">The command could not be sent (the connection is unusable afterwards).</exception>
    public async Task SendBodyAsync(string messageId, CancellationToken cancellationToken)
    {
        var id = NormalizeMessageId(messageId);
        ThrowIfUnusable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await WriteLineAsync($"BODY <{id}>", timeout);
        }
        catch (Exception ex)
        {
            throw Fail(ex, cancellationToken);
        }
    }

    /// <summary>Pipelining, second half: reads the answer to the oldest BODY not read yet. Same results as <see cref="BodyAsync"/>.</summary>
    public async Task<DecodedArticle> ReceiveBodyAsync(string messageId, CancellationToken cancellationToken)
    {
        var id = NormalizeMessageId(messageId);
        ThrowIfUnusable();
        using var body = new PooledBuffer(ArticleBufferSize);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                var response = await ReadResponseAsync(timeout);
                switch (response.Code)
                {
                    case 222:
                        break;
                    case 430 or 423:
                        throw new NntpArticleNotFoundException(id, response.Text);
                    case 480:
                        throw new NntpException($"{Endpoint} wants a login: {response}", NntpErrorKind.Auth, response.Code);
                    case 400:
                        _broken = true; // 400 = the server is about to close the connection
                        throw new NntpException($"{Endpoint} is not available: {response}", NntpErrorKind.Busy, response.Code);
                    case < 400:
                        _broken = true; // a positive answer we did not ask for: out of sync
                        throw new NntpException($"Unexpected answer to BODY from {Endpoint}: {response}", NntpErrorKind.Protocol, response.Code);
                    default:
                        throw new NntpException($"{Endpoint} refused BODY: {response}", NntpErrorKind.Protocol, response.Code);
                }

                while (!TryReadMultiline(body))
                {
                    await FillAsync(timeout);
                }
            }
            catch (Exception ex) when (ex is not NntpArticleNotFoundException)
            {
                throw Fail(ex, cancellationToken);
            }
        }

        var part = YencDecoder.Decode(body.Span);
        return new DecodedArticle(part.Name, part.FileSize, part.Offset, part.Data, part.IsValid);
    }

    /// <summary>STAT &lt;message-id&gt;: true when the server has the article (223), false when not (430/423). Nothing is downloaded.</summary>
    /// <exception cref="NntpException">Any other answer or failure.</exception>
    public async Task<bool> StatAsync(string messageId, CancellationToken cancellationToken)
    {
        await SendStatAsync(messageId, cancellationToken);
        return await ReceiveStatAsync(cancellationToken);
    }

    /// <summary>Pipelining, first half of <see cref="StatAsync"/>: sends STAT without waiting for the answer.</summary>
    public async Task SendStatAsync(string messageId, CancellationToken cancellationToken)
    {
        var id = NormalizeMessageId(messageId);
        ThrowIfUnusable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await WriteLineAsync($"STAT <{id}>", timeout);
        }
        catch (Exception ex)
        {
            throw Fail(ex, cancellationToken);
        }
    }

    /// <summary>Pipelining, second half of <see cref="StatAsync"/>: reads the answer to the oldest STAT.</summary>
    public async Task<bool> ReceiveStatAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var response = await ReadResponseAsync(timeout);
            return response.Code switch
            {
                223 => true,
                430 or 423 => false,
                480 => throw new NntpException($"{Endpoint} wants a login: {response}", NntpErrorKind.Auth, response.Code),
                400 => throw new NntpException($"{Endpoint} is not available: {response}", NntpErrorKind.Busy, response.Code),
                _ => throw new NntpException($"{Endpoint} refused STAT: {response}", NntpErrorKind.Protocol, response.Code),
            };
        }
        catch (Exception ex)
        {
            throw Fail(ex, cancellationToken);
        }
    }

    /// <summary>DATE (111): the server's clock in UTC. A cheap command to check that the connection works.</summary>
    public async Task<DateTimeOffset> DateAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await WriteLineAsync("DATE", timeout);
            var response = await ReadResponseAsync(timeout);
            if (response.Code != 111)
            {
                throw new NntpException($"Unexpected answer to DATE from {Endpoint}: {response}", NntpErrorKind.Protocol, response.Code);
            }

            var text = response.Text.Split(' ', 2)[0];
            return DateTimeOffset.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date
                : throw new NntpException($"Unreadable answer to DATE from {Endpoint}: {response}", NntpErrorKind.Protocol, response.Code);
        }
        catch (Exception ex)
        {
            throw Fail(ex, cancellationToken);
        }
    }

    /// <summary>
    /// Marks the connection unusable without closing it, e.g. when a pipelined answer will never be read: the answers
    /// after it would be out of step. Commands already reading finish; new ones fail with <see cref="NntpErrorKind.Closed"/>.
    /// </summary>
    public void MarkBroken() => _broken = true;

    /// <summary>Says goodbye (QUIT, waits up to 2 seconds for 205). Never throws; the connection is unusable afterwards.</summary>
    public async Task QuitAsync()
    {
        if (!IsUsable)
        {
            return;
        }

        _broken = true;
        try
        {
            using var timeout = new CancellationTokenSource(QuitTimeout);
            await WriteLineAsync("QUIT", timeout);
            await ReadResponseAsync(timeout);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or NntpException or AuthenticationException)
        {
            // The server may close first; nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _broken = true;
        try
        {
            await _stream.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or AuthenticationException)
        {
            // Closing a dead TLS stream can fail; the socket is gone either way.
        }

        var buffer = _buffer;
        _buffer = [];
        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>Accepts "id@host" and "&lt;id@host&gt;"; rejects anything that could break the command line.</summary>
    public static string NormalizeMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var id = messageId.Trim();
        if (id.StartsWith('<') && id.EndsWith('>'))
        {
            id = id[1..^1];
        }

        if (id.Length == 0 || id.Length > 250 || id.Any(c => c <= ' ' || c == '<' || c == '>' || c == 127))
        {
            throw new ArgumentException($"'{messageId}' is not a valid message-id.", nameof(messageId));
        }

        return id;
    }

    private static async Task HandshakeTlsAsync(SslStream ssl, NntpConnectionSettings settings, string endpoint, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        const string hint = "Check that this port uses SSL (usually 563, or 119 without SSL).";
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = settings.Host,
                    RemoteCertificateValidationCallback = settings.CertificateValidation,
                },
                timeoutToken);
        }
        catch (AuthenticationException ex)
        {
            var reason = ex.InnerException?.Message is { Length: > 0 } inner ? $"{ex.Message} ({inner})" : ex.Message;
            throw new NntpException($"The SSL connection to {endpoint} failed: {reason}", NntpErrorKind.Tls, inner: ex);
        }
        catch (IOException ex)
        {
            throw new NntpException($"The SSL connection to {endpoint} failed: {ex.Message} {hint}", NntpErrorKind.Tls, inner: ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NntpException($"{endpoint} did not complete the SSL handshake within {settings.ConnectTimeout.TotalSeconds:0} seconds. {hint}", NntpErrorKind.Tls, inner: ex);
        }
    }

    private static string? TlsName(SslProtocols protocol) => protocol switch
    {
#pragma warning disable SYSLIB0039 // only for display
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => "TLS",
    };

    private async Task GreetAndLoginAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var greeting = await ReadResponseAsync(timeout);
            Greeting = greeting.Text;
            switch (greeting.Code)
            {
                case 200 or 201:
                    break;
                case 400:
                    throw new NntpException($"{Endpoint} is busy: {greeting}", NntpErrorKind.Busy, greeting.Code);
                case 502 when MentionsConnections(greeting.Text):
                    throw new NntpException($"{Endpoint} refused the connection: {greeting}", NntpErrorKind.Busy, greeting.Code);
                case 502:
                    throw new NntpException($"{Endpoint} refused the connection: {greeting}", NntpErrorKind.Rejected, greeting.Code);
                default:
                    throw new NntpException($"Unexpected greeting from {Endpoint}: {greeting}", NntpErrorKind.Protocol, greeting.Code);
            }

            if (string.IsNullOrEmpty(_settings.Username))
            {
                return;
            }

            if (_settings.Username.Any(char.IsControl) || _settings.Password.Any(char.IsControl))
            {
                throw new NntpException("The user name or password contains a line break or another control character.", NntpErrorKind.Auth);
            }

            await WriteLineAsync($"AUTHINFO USER {_settings.Username}", timeout);
            var response = await ReadResponseAsync(timeout);
            if (response.Code == 381)
            {
                await WriteLineAsync($"AUTHINFO PASS {_settings.Password}", timeout);
                response = await ReadResponseAsync(timeout);
            }

            if (response.Code != 281)
            {
                throw LoginFailed(response);
            }
        }
        catch (Exception ex)
        {
            throw Fail(ex, cancellationToken);
        }
    }

    private NntpException LoginFailed(NntpResponse response)
    {
        if (response.Code == 400 || MentionsConnections(response.Text))
        {
            return new NntpException($"{Endpoint} refused the login: {response}", NntpErrorKind.Busy, response.Code);
        }

        return response.Code is 481 or 482 or 480 or 502 or 452
            ? new NntpException($"Login failed: {response}", NntpErrorKind.Auth, response.Code)
            : new NntpException($"Unexpected answer to the login from {Endpoint}: {response}", NntpErrorKind.Protocol, response.Code);
    }

    // "502 Too many connections", "481 Connection limit reached" and similar: the account is fine, it is just busy.
    private static bool MentionsConnections(string text) =>
        text.Contains("connection", StringComparison.OrdinalIgnoreCase) || text.Contains("too many", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfUnusable()
    {
        if (!IsUsable)
        {
            throw new NntpException($"The connection to {Endpoint} is closed.", NntpErrorKind.Closed);
        }
    }

    /// <summary>Marks the connection broken and turns low-level errors into <see cref="NntpException"/>; cancellation stays cancellation.</summary>
    private Exception Fail(Exception error, CancellationToken cancellationToken)
    {
        _broken = true;
        return error switch
        {
            NntpException or YencException => error,
            OperationCanceledException when cancellationToken.IsCancellationRequested => error,
            OperationCanceledException => new NntpException(
                $"{Endpoint} did not answer within {_settings.ReadTimeout.TotalSeconds:0} seconds.", NntpErrorKind.Timeout, inner: error),
            IOException or SocketException or ObjectDisposedException or AuthenticationException => new NntpException(
                $"The connection to {Endpoint} was lost: {error.Message}", NntpErrorKind.Closed, inner: error),
            _ => error,
        };
    }

    private async ValueTask WriteLineAsync(string line, CancellationTokenSource timeout)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        timeout.CancelAfter(_settings.ReadTimeout);
        await _stream.WriteAsync(bytes, timeout.Token);
        await _stream.FlushAsync(timeout.Token);
    }

    private async ValueTask<NntpResponse> ReadResponseAsync(CancellationTokenSource timeout)
    {
        NntpResponse response;
        while (!TryReadStatusLine(out response))
        {
            await FillAsync(timeout);
        }

        return response;
    }

    /// <summary>Reads more data into the buffer, compacting or growing it first. Every read gets the full read timeout.</summary>
    private async ValueTask FillAsync(CancellationTokenSource timeout)
    {
        if (_start > 0)
        {
            var remaining = _end - _start;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
            }

            _start = 0;
            _end = remaining;
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= MaxArticleBytes)
            {
                throw new NntpException($"{Endpoint} sent a line longer than {MaxArticleBytes / 1024 / 1024} MB.", NntpErrorKind.Protocol);
            }

            var larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        timeout.CancelAfter(_settings.ReadTimeout);
        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), timeout.Token);
        if (read == 0)
        {
            throw new NntpException($"{Endpoint} closed the connection.", NntpErrorKind.Closed);
        }

        _end += read;
        BytesReceived += read;
    }

    private bool TryReadStatusLine(out NntpResponse response)
    {
        var data = _buffer.AsSpan(_start, _end - _start);
        var newline = data.IndexOf((byte)'\n');
        if (newline < 0)
        {
            if (data.Length > MaxStatusLineLength)
            {
                throw new NntpException($"{Endpoint} sent an overlong status line.", NntpErrorKind.Protocol);
            }

            response = default;
            return false;
        }

        var line = data[..newline];
        _start += newline + 1;
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.Length < 3 || !int.TryParse(line[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var code) || (line.Length > 3 && line[3] != (byte)' '))
        {
            throw new NntpException($"{Endpoint} sent an invalid status line: {Encoding.UTF8.GetString(line[..Math.Min(line.Length, 100)])}", NntpErrorKind.Protocol);
        }

        var text = line.Length > 4 ? Encoding.UTF8.GetString(line[4..]).Trim() : string.Empty;
        response = new NntpResponse(code, text);
        return true;
    }

    /// <summary>
    /// Moves all complete lines of a multi-line block from the read buffer into <paramref name="output"/> (LF-terminated, dot-unstuffed).
    /// True when the terminating "." line was reached.
    /// </summary>
    private bool TryReadMultiline(PooledBuffer output)
    {
        while (true)
        {
            var data = _buffer.AsSpan(_start, _end - _start);
            var newline = data.IndexOf((byte)'\n');
            if (newline < 0)
            {
                return false;
            }

            var line = data[..newline];
            _start += newline + 1;
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.Length > 0 && line[0] == (byte)'.')
            {
                if (line.Length == 1)
                {
                    return true;
                }

                line = line[1..]; // ".." at the start of a line was a stuffed "."
            }

            if (output.Length + line.Length + 1 > MaxArticleBytes)
            {
                throw new NntpException($"{Endpoint} sent an article larger than {MaxArticleBytes / 1024 / 1024} MB.", NntpErrorKind.Protocol);
            }

            output.Append(line);
            output.Append((byte)'\n');
        }
    }
}
