// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace StreamZH.Tests.Usenet;

/// <summary>
/// An in-process NNTP server on 127.0.0.1 for tests: greeting, AUTHINFO USER/PASS, BODY with dot-stuffing, DATE, QUIT,
/// optional TLS with a self-signed certificate, plus slow, stalling and disconnecting modes. Counts connections.
/// </summary>
public sealed class FakeNntpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte[]> _articles = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private int _current;
    private int _maxConcurrent;
    private int _opened;
    private int _bodyRequests;
    private int _bodiesReceived;
    private int _dropNextBodies;
    private int _disposed;

    public FakeNntpServer(X509Certificate2? certificate = null)
    {
        Certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public X509Certificate2? Certificate { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Null: no login needed.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string Greeting { get; set; } = "200 StreamZH fake news server ready";

    /// <summary>Wait before answering BODY.</summary>
    public TimeSpan BodyDelay { get; set; }

    /// <summary>Never answer BODY (read timeout tests).</summary>
    public bool StallBodies { get; set; }

    public int ConnectionsOpened => Volatile.Read(ref _opened);

    public int CurrentConnections => Volatile.Read(ref _current);

    public int MaxConcurrentConnections => Volatile.Read(ref _maxConcurrent);

    public int BodyRequests => Volatile.Read(ref _bodyRequests);

    /// <summary>BODY commands that arrived, answered or not (more than <see cref="BodyRequests"/> while pipelined ones wait).</summary>
    public int BodiesReceived => Volatile.Read(ref _bodiesReceived);

    /// <summary>While set and not completed, BODY answers wait for it.</summary>
    public TaskCompletionSource? BodyGate { get; set; }

    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>The next <paramref name="count"/> BODY answers stop halfway and the connection is closed.</summary>
    public void DropNextBodies(int count) => Volatile.Write(ref _dropNextBodies, count);

    /// <summary>Stores an article body (CRLF lines, not dot-stuffed; the server stuffs when sending).</summary>
    public void Add(string messageId, byte[] body) => _articles[messageId.Trim('<', '>')] = body;

    /// <summary>Stores <paramref name="data"/> yEnc encoded as one part of a file.</summary>
    public void AddYenc(string messageId, byte[] data, string name = "file.bin", int part = 1, int total = 1, long begin = 0, long? fileSize = null, uint? crcOverride = null) =>
        Add(messageId, YencArticles.Encode(data, name, part, total, begin, fileSize ?? begin + data.Length, crcOverride));

    public static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A validation callback that trusts exactly this certificate (only the untrusted-root error is forgiven, the name must match).</summary>
    public static RemoteCertificateValidationCallback Trust(X509Certificate2 expected) => (_, certificate, _, errors) =>
        certificate is not null
        && certificate.GetCertHashString() == expected.GetCertHashString()
        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _clients.Add(client);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _opened);
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
        {
        }

        var token = _stop.Token;
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
                if (Certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(Certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                    stream = ssl;
                }

                await using (stream)
                {
                    using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
                    await WriteAsync(stream, Greeting, token);
                    if (!Greeting.StartsWith('2'))
                    {
                        return;
                    }

                    var loggedIn = Username is null;
                    string? user = null;
                    // Lines are read as they arrive (so pipelined commands can be counted) and answered one by one.
                    var lines = Channel.CreateUnbounded<string>();
                    _ = Task.Run(() => ReadLinesAsync(reader, lines.Writer, token), CancellationToken.None);
                    await foreach (var line in lines.Reader.ReadAllAsync(token))
                    {
                        Commands.Enqueue(line);
                        var parts = line.Split(' ', 3);
                        switch (parts[0].ToUpperInvariant())
                        {
                            case "AUTHINFO" when parts.Length == 3 && parts[1].Equals("USER", StringComparison.OrdinalIgnoreCase):
                                user = parts[2];
                                await WriteAsync(stream, "381 Password required", token);
                                break;
                            case "AUTHINFO" when parts.Length == 3 && parts[1].Equals("PASS", StringComparison.OrdinalIgnoreCase):
                                loggedIn = user == Username && parts[2] == Password;
                                await WriteAsync(stream, loggedIn ? "281 Authentication accepted" : "481 Authentication failed", token);
                                break;
                            case "BODY" when !loggedIn:
                                await WriteAsync(stream, "480 Authentication required", token);
                                break;
                            case "BODY" when parts.Length >= 2:
                                if (!await SendBodyAsync(stream, parts[1].Trim('<', '>'), token))
                                {
                                    return;
                                }

                                break;
                            case "STAT" when !loggedIn:
                                await WriteAsync(stream, "480 Authentication required", token);
                                break;
                            case "STAT" when parts.Length >= 2:
                                var statId = parts[1].Trim('<', '>');
                                await WriteAsync(stream, _articles.ContainsKey(statId) ? $"223 0 <{statId}>" : "430 No such article", token);
                                break;
                            case "DATE":
                                await WriteAsync(stream, "111 20261017120000", token);
                                break;
                            case "QUIT":
                                await WriteAsync(stream, "205 Bye", token);
                                return;
                            default:
                                await WriteAsync(stream, "500 What?", token);
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
        {
            // Client went away or the test ended.
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private async Task ReadLinesAsync(StreamReader reader, ChannelWriter<string> lines, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.StartsWith("BODY", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _bodiesReceived);
                }

                await lines.WriteAsync(line, token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
        {
            // Connection closed.
        }
        finally
        {
            lines.TryComplete();
        }
    }

    /// <returns>False when the connection was dropped on purpose.</returns>
    private async Task<bool> SendBodyAsync(Stream stream, string messageId, CancellationToken token)
    {
        Interlocked.Increment(ref _bodyRequests);
        if (StallBodies)
        {
            await Task.Delay(Timeout.Infinite, token);
        }

        if (BodyGate is { } gate)
        {
            await gate.Task.WaitAsync(token);
        }

        if (BodyDelay > TimeSpan.Zero)
        {
            await Task.Delay(BodyDelay, token);
        }

        if (!_articles.TryGetValue(messageId, out var body))
        {
            await WriteAsync(stream, "430 No Such Article", token);
            return true;
        }

        var stuffed = Stuff(body);
        int left;
        do
        {
            left = Volatile.Read(ref _dropNextBodies);
        }
        while (left > 0 && Interlocked.CompareExchange(ref _dropNextBodies, left - 1, left) != left);

        if (left > 0)
        {
            await WriteAsync(stream, $"222 0 <{messageId}>", token);
            await stream.WriteAsync(stuffed.AsMemory(0, stuffed.Length / 2), token);
            await stream.FlushAsync(token);
            return false;
        }

        await WriteAsync(stream, $"222 0 <{messageId}>", token);
        await stream.WriteAsync(stuffed, token);
        await stream.WriteAsync(".\r\n"u8.ToArray(), token);
        await stream.FlushAsync(token);
        return true;
    }

    private static async Task WriteAsync(Stream stream, string line, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), token);
        await stream.FlushAsync(token);
    }

    /// <summary>RFC 3977 dot-stuffing: every line that starts with "." gets another one.</summary>
    private static byte[] Stuff(byte[] body)
    {
        var output = new MemoryStream(body.Length + 1024);
        var atLineStart = true;
        foreach (var b in body)
        {
            if (atLineStart && b == (byte)'.')
            {
                output.WriteByte((byte)'.');
            }

            output.WriteByte(b);
            atLineStart = b == (byte)'\n';
        }

        if (!atLineStart)
        {
            output.Write("\r\n"u8);
        }

        return output.ToArray();
    }
}

/// <summary>yEnc encoder for tests. Unlike most posters it does not escape a leading ".", so dot-stuffing gets exercised.</summary>
public static class YencArticles
{
    public const int LineLength = 128;

    public static byte[] Encode(byte[] data, string name, int part, int total, long begin, long fileSize, uint? crcOverride = null)
    {
        var buffer = new MemoryStream();
        void Write(string s) => buffer.Write(Encoding.Latin1.GetBytes(s));

        var crc = (crcOverride ?? Crc32.HashToUInt32(data)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        Write($"=ybegin part={part} total={total} line={LineLength} size={fileSize} name={name}\r\n");
        Write($"=ypart begin={begin + 1} end={begin + data.Length}\r\n");

        var column = 0;
        foreach (var b in data)
        {
            var c = (byte)(b + 42);
            if (c is 0 or 10 or 13 or 61 || (column == 0 && c is 9 or 32))
            {
                buffer.WriteByte((byte)'=');
                c = (byte)(c + 64);
                column++;
            }

            buffer.WriteByte(c);
            if (++column >= LineLength)
            {
                Write("\r\n");
                column = 0;
            }
        }

        if (column > 0)
        {
            Write("\r\n");
        }

        Write($"=yend size={data.Length} part={part} pcrc32={crc}\r\n");
        return buffer.ToArray();
    }

    /// <summary>Bytes that encode to "." (4 + 42 = 46), so every line of the article starts with a dot.</summary>
    public static byte[] Dots(int count) => Enumerable.Repeat((byte)4, count).ToArray();

    public static byte[] Random(int count, int seed = 1)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }
}
