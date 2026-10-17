// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StreamZH.Tests.Connect;

/// <summary>A tiny plain-text SMTP server on localhost: EHLO with AUTH PLAIN LOGIN, MAIL, RCPT, DATA, QUIT.</summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly StringBuilder _data = new();

    public FakeSmtpServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool RejectLogin { get; init; }

    public IReadOnlyList<string> Commands => [.. _commands];

    public string Data
    {
        get
        {
            lock (_data)
            {
                return _data.ToString();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.smtp ESMTP");
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    _commands.Enqueue(line);
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake.smtp");
                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "AUTH":
                            await writer.WriteLineAsync(RejectLogin ? "535 5.7.8 Authentication credentials invalid" : "235 2.7.0 Authentication successful");
                            break;
                        case "MAIL":
                        case "RCPT":
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                            {
                                lock (_data)
                                {
                                    _data.AppendLine(dataLine);
                                }
                            }

                            await writer.WriteLineAsync("250 OK queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 OK");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}
