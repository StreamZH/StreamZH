// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using StreamZH.Core;

namespace StreamZH.Library.Notifications;

/// <param name="Encryption">"preferred" (when offered), "always" (SSL on 465, STARTTLS otherwise) or "never".</param>
public sealed record EmailServer(string Host, int Port, string Encryption, string? Username, string? Password);

/// <summary>Delivers a finished message to an SMTP server (MailKit; tests replace it).</summary>
public interface IEmailTransport
{
    /// <exception cref="ConnectException">Connection, encryption, login or sending failed.</exception>
    Task SendAsync(EmailServer server, MimeMessage message, CancellationToken cancellationToken);
}

public sealed class MailKitTransport : IEmailTransport
{
    public static SecureSocketOptions Options(EmailServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Encryption switch
        {
            "never" => SecureSocketOptions.None,
            "always" => server.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            _ => server.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
        };
    }

    public async Task SendAsync(EmailServer server, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(server.Host, server.Port, Options(server), cancellationToken);
            if (!string.IsNullOrEmpty(server.Username))
            {
                await client.AuthenticateAsync(server.Username, server.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (AuthenticationException ex)
        {
            throw new ConnectException("unauthorized, check user name and password", inner: ex);
        }
        catch (SslHandshakeException ex)
        {
            throw new ConnectException("SSL failed, check port and encryption", inner: ex);
        }
        catch (SmtpCommandException ex)
        {
            // 4xx answers are temporary (greylisting, busy), 5xx are final.
            throw new ConnectException($"rejected: {ConnectHttp.Short(ex.Message)}", retryable: (int)ex.StatusCode < 500, ex);
        }
        catch (SmtpProtocolException ex)
        {
            throw new ConnectException($"mail server error: {ConnectHttp.Short(ex.Message)}", retryable: true, ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ServiceNotConnectedException)
        {
            throw new ConnectException($"not reachable ({ConnectHttp.Short(ex.Message)})", retryable: true, ex);
        }
    }
}

/// <summary>Plain-text email with the summary, size, release and links.</summary>
public sealed class EmailSender(IEmailTransport transport) : IConnectionSender, IConnectionValidator
{
    public string Implementation => ConnectCatalog.Email;

    public Task SendAsync(ConnectionContext context, ConnectEvent connectEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return transport.SendAsync(Server(context.Settings), Message(context, connectEvent), cancellationToken);
    }

    public async Task<string> TestAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        await SendAsync(context, ConnectEvent.Test(), cancellationToken);
        return "Test email sent.";
    }

    public void Validate(ConnectionSettings settings, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errors);
        if (settings.Text("from") is { } from && !MailboxAddress.TryParse(from, out _))
        {
            errors.TryAdd("from", ["Enter an email address, e.g. streamzh@example.com."]);
        }

        foreach (var field in new[] { "to", "cc", "bcc" })
        {
            if (settings.List(field).FirstOrDefault(a => !MailboxAddress.TryParse(a, out _)) is { } invalid)
            {
                errors.TryAdd(field, [$"{invalid} is not an email address."]);
            }
        }
    }

    public static EmailServer Server(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new EmailServer(
            settings.Text("server") ?? throw new ConnectException("server is missing"),
            settings.Int("port") ?? 587,
            settings.Text("useEncryption") ?? "preferred",
            settings.Text("username"),
            settings.Text("password"));
    }

    public static MimeMessage Message(ConnectionContext context, ConnectEvent e)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(e);
        var settings = context.Settings;
        var message = new MimeMessage();
        message.From.Add(Address(settings.Text("from") ?? throw new ConnectException("from address is missing")));
        foreach (var to in settings.List("to"))
        {
            message.To.Add(Address(to));
        }

        foreach (var cc in settings.List("cc"))
        {
            message.Cc.Add(Address(cc));
        }

        foreach (var bcc in settings.List("bcc"))
        {
            message.Bcc.Add(Address(bcc));
        }

        if (message.To.Count == 0)
        {
            throw new ConnectException("no recipient set");
        }

        var title = e.Media?.Display ?? e.Health?.Title;
        message.Subject = title is null ? $"{AppInfo.Name} - {e.Subject}" : $"{AppInfo.Name} - {e.Subject}: {title}";
        message.Body = new TextPart("plain") { Text = ConnectText.Body(e, withLinks: true) + $"\n\n-- \n{context.InstanceName}" };
        return message;
    }

    private static MailboxAddress Address(string text) =>
        MailboxAddress.TryParse(text, out var address) ? address : throw new ConnectException($"{text} is not an email address");
}

/// <summary>Extra checks of a type's settings beyond the field rules, run before saving and testing.</summary>
public interface IConnectionValidator
{
    void Validate(ConnectionSettings settings, Dictionary<string, string[]> errors);
}
