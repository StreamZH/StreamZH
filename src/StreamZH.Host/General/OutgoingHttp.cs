// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Net;
using System.Net.Security;
using Microsoft.Extensions.Http;
using StreamZH.Host.Auth;

namespace StreamZH.Host.General;

/// <summary>
/// The proxy of Settings → General, read on every request (changes apply without a restart). While it is off, the
/// system proxy (HTTP_PROXY, HTTPS_PROXY, NO_PROXY or the Windows settings) is used as before.
/// </summary>
public sealed class SettingsWebProxy(ProxySettings settings) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => settings.ProxyUri is null
            ? HttpClient.DefaultProxy.Credentials
            : settings.Username is { } user ? new NetworkCredential(user, settings.Password ?? string.Empty) : null;
        set
        {
            // Credentials come from the settings.
        }
    }

    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return settings.ProxyUri is { } proxy
            ? settings.IsBypassed(destination.IdnHost) ? null : proxy
            : HttpClient.DefaultProxy.GetProxy(destination);
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return settings.ProxyUri is null ? HttpClient.DefaultProxy.IsBypassed(host) : settings.IsBypassed(host.IdnHost);
    }
}

/// <summary>Settings → General → Certificate validation for outgoing HTTPS.</summary>
public sealed class CertificateValidator(SecuritySettings security)
{
    public bool Validate(string? host, SslPolicyErrors errors) => errors == SslPolicyErrors.None || security.CertificateValidation switch
    {
        CertificateValidation.Disabled => true,
        CertificateValidation.DisabledForLocalAddresses => LocalAddress.IsLocalHost(host),
        _ => false,
    };
}

/// <summary>
/// Puts the proxy and the certificate check on the primary handler of every HttpClient from IHttpClientFactory
/// (TMDB, indexers, tool downloads, update check, Prowlarr import). A filter runs after each client's own setup,
/// so it also reaches clients that bring their own SocketsHttpHandler. Handlers that are not socket based (test fakes)
/// and clients that switched the proxy off are left alone.
/// </summary>
public sealed class OutgoingHttpFilter(SettingsWebProxy proxy, CertificateValidator certificates) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        Apply(builder.PrimaryHandler);
    };

    public void Apply(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                if (sockets.UseProxy && sockets.Proxy is null)
                {
                    sockets.Proxy = proxy;
                }

                sockets.SslOptions.RemoteCertificateValidationCallback ??= (sender, _, _, errors) =>
                    certificates.Validate((sender as SslStream)?.TargetHostName, errors);
                break;

            case HttpClientHandler client:
                if (client.UseProxy && client.Proxy is null)
                {
                    client.Proxy = proxy;
                }

                client.ServerCertificateCustomValidationCallback ??= (request, _, _, errors) => certificates.Validate(request.RequestUri?.IdnHost, errors);
                break;
        }
    }
}
