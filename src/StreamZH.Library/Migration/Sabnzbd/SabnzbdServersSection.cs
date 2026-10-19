// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StreamZH.Core.Downloads;
using StreamZH.Library.Search;
using StreamZH.Library.Usenet;

namespace StreamZH.Library.Migration.Sabnzbd;

/// <summary>
/// SABnzbd's Usenet servers → StreamZH's Usenet servers (Settings → Usenet). SABnzbd never hands out passwords: each
/// server with a password needs it typed in (<see cref="MigrationItem.NeedsSecret"/>); without it the server is added
/// switched off. Servers are matched by host, port and user name, so a second run updates them.
/// <para>
/// While SABnzbd keeps running, both share the provider's connection limit: option "servers.connections" = "half"
/// (default while the side-by-side mode is on) gives StreamZH half of SABnzbd's connections, "same" all of them.
/// </para>
/// </summary>
public sealed class SabnzbdServersSection : IMigrationSection
{
    public const string SectionKey = "servers";
    public const string ConnectionsOption = "servers.connections";
    public const string Half = "half";
    public const string Same = "same";

    public string Source => MigrationSources.Sabnzbd;

    public string Key => SectionKey;

    public string Label => "Usenet servers";

    public string Description => "Your Usenet provider accounts: address, SSL, login, connections, priority and retention. Type in the passwords; SABnzbd does not hand them out.";

    public int Order => 360;

    public bool DefaultSelected => true;

    public bool Advanced => false;

    public static string ItemKey(SabnzbdServer server) => $"{SectionKey}:{server.Name}";

    /// <summary>"half" while side-by-side (the old downloader still uses the account), else "same"; the wizard can choose.</summary>
    public static string ConnectionsMode(MigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fallback = context.Services.GetService<AutomationHold>()?.IsOn ?? true ? Half : Same;
        var mode = context.Option(ConnectionsOption, fallback).Trim().ToLowerInvariant();
        return mode is Half or Same ? mode : fallback;
    }

    public static int Connections(int sabnzbd, string mode)
    {
        var all = Math.Clamp(sabnzbd, UsenetServerService.MinConnections, UsenetServerService.MaxConnections);
        return mode == Half ? Math.Max(UsenetServerService.MinConnections, all / 2) : all;
    }

    public async Task<MigrationSectionPlan> PreviewAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var service = context.Services.GetRequiredService<UsenetServerService>();
        var existing = service.All();
        var mode = ConnectionsMode(context);
        var items = new List<MigrationItem>();
        foreach (var server in config.Servers)
        {
            var match = Match(existing, server);
            var connections = Connections(server.Connections, mode);
            var detail = Detail(server, connections, mode);
            if (string.IsNullOrWhiteSpace(server.Host))
            {
                items.Add(new MigrationItem(ItemKey(server), server.Label, MigrationAction.Skip, detail, "No server address in SABnzbd."));
                continue;
            }

            var needsSecret = server.HasMaskedPassword && (match is null || string.IsNullOrEmpty(match.Password));
            string? warning = null;
            if (needsSecret)
            {
                warning = match is null
                    ? "Type in the password. Without it the server is added switched off."
                    : "Type in the password; StreamZH has none stored for this server yet.";
            }
            else if (match is not null && server.HasMaskedPassword)
            {
                detail += " · keeps the password stored in StreamZH";
            }

            if (!server.Enable)
            {
                detail += " · switched off in SABnzbd";
            }

            items.Add(new MigrationItem(
                ItemKey(server),
                server.Label,
                match is null ? MigrationAction.Create : MigrationAction.Update,
                match is null ? detail : $"{detail} · updates \"{match.Name}\"",
                Warning: warning,
                NeedsSecret: needsSecret));
        }

        var warnings = new List<string>();
        if (config.Servers.Count > 0)
        {
            warnings.Add(mode == Half
                ? "While SABnzbd keeps running, both share your provider's connection limit. StreamZH takes half of SABnzbd's connections; choose \"same\" once SABnzbd is stopped (or raise them in Settings → Usenet)."
                : "StreamZH takes as many connections as SABnzbd. While SABnzbd keeps running, both together may go over your provider's limit; choose \"half\" until you stop SABnzbd.");
        }

        return new MigrationSectionPlan(items, warnings);
    }

    public async Task RunAsync(MigrationContext context, MigrationSectionRun run, CancellationToken cancellationToken)
    {
        var config = await context.Services.GetRequiredService<SabnzbdApiClient>().ConfigAsync(context.Connection, cancellationToken);
        var service = context.Services.GetRequiredService<UsenetServerService>();
        var mode = ConnectionsMode(context);
        var done = 0;
        foreach (var server in config.Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.Progress(done++, config.Servers.Count, server.Label);
            var key = ItemKey(server);
            if (context.IsExcluded(key))
            {
                run.Skipped(server.Label, "Left out in the wizard.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(server.Host))
            {
                run.Skipped(server.Label, "No server address in SABnzbd.");
                continue;
            }

            var existing = service.All();
            var match = Match(existing, server);
            var typed = context.Secret(key);

            // The password: typed in, none needed (SABnzbd has none), or kept (update) / missing (new server).
            string? password = !server.HasMaskedPassword ? server.Password : typed;
            var hasPassword = password is not null || !string.IsNullOrEmpty(match?.Password);
            var mapped = Map(server, Connections(server.Connections, mode)) with
            {
                Enabled = server.Enable && hasPassword,
            };

            try
            {
                UsenetServer saved;
                if (match is null)
                {
                    saved = service.Create(mapped with { Name = UniqueName(existing, mapped.Name), Password = password ?? string.Empty });
                    run.Created(server.Label);
                }
                else
                {
                    saved = service.Update(match.Id, mapped with { Name = match.Name, Password = password ?? UsenetServerService.MaskedPassword, Notes = Notes(match, server) });
                    run.Updated(server.Label);
                }

                context.Ids.Set("usenetServer", server.Name, saved.Id);
                if (!hasPassword && server.Enable)
                {
                    run.Warn($"{saved.Name}: no password, so it was added switched off. Enter the password in Settings → Usenet and switch it on.", server.Label);
                }
            }
            catch (ValidationFailedException ex)
            {
                run.Failed(server.Label, string.Join(" ", ex.Errors.Values.SelectMany(v => v)));
            }
        }

        if (config.Servers.Count > 0 && mode == Half)
        {
            run.Info("StreamZH uses half of SABnzbd's connections while both run. Raise them in Settings → Usenet once SABnzbd is stopped.");
        }

        run.Progress(config.Servers.Count, config.Servers.Count);
    }

    /// <summary>The StreamZH server for a SABnzbd server (password and name are set by the caller).</summary>
    public static UsenetServer Map(SabnzbdServer server, int connections)
    {
        ArgumentNullException.ThrowIfNull(server);
        var quota = SabnzbdConfig.FromUnits(server.Quota) is { } bytes and > 0 ? (long?)Math.Round(bytes) : null;
        return new UsenetServer(
            0,
            server.Label.Trim(),
            server.Host.Trim(),
            server.Port is > 0 and <= 65535 ? server.Port : server.Ssl ? 563 : 119,
            server.Ssl,
            server.Username.Trim(),
            string.Empty,
            connections,
            Math.Clamp(server.Priority, 0, UsenetServerService.MaxPriority),
            server.Enable,
            Math.Max(0, server.Retention),
            Math.Clamp(server.Timeout, UsenetServerService.MinTimeoutSeconds, UsenetServerService.MaxTimeoutSeconds),

            // SABnzbd: 0 none, 1 minimal (chain only), 2 medium, 3 strict. StreamZH's Minimal also checks the name.
            server.SslVerify switch
            {
                <= 0 => CertificateCheck.Off,
                1 => CertificateCheck.Minimal,
                _ => CertificateCheck.Strict,
            },
            Math.Clamp(server.PipeliningRequests, 1, UsenetServerService.MaxArticlesPerRequest),

            // "Required" pauses the queue while the server is down; "Optional" is an unreliable backup.
            server.Required ? ServerFailureAction.PauseDownloads : server.Optional ? ServerFailureAction.IgnoreLonger : ServerFailureAction.Skip,
            DateOnly.TryParseExact(server.ExpireDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires)
                ? expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            quota,
            Notes: server.Notes.Trim());
    }

    /// <summary>Same host (case-insensitive), port and user name.</summary>
    public static UsenetServer? Match(IEnumerable<UsenetServer> existing, SabnzbdServer server)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(server);
        return existing.FirstOrDefault(s =>
            string.Equals(s.Host.Trim(), server.Host.Trim(), StringComparison.OrdinalIgnoreCase)
            && s.Port == server.Port
            && string.Equals(s.Username.Trim(), server.Username.Trim(), StringComparison.Ordinal));
    }

    private static string Detail(SabnzbdServer server, int connections, string mode)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{server.Host}:{server.Port}{(server.Ssl ? " SSL" : string.Empty)}"),
            connections == server.Connections
                ? string.Create(CultureInfo.InvariantCulture, $"{connections} connections")
                : string.Create(CultureInfo.InvariantCulture, $"{server.Connections} → {connections} connections{(mode == Half ? " (half, while SABnzbd runs)" : string.Empty)}"),
            server.Priority == 0 ? "primary" : string.Create(CultureInfo.InvariantCulture, $"backup (priority {server.Priority})"),
        };
        if (server.Retention > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"retention {server.Retention} days"));
        }

        if (server.Optional)
        {
            parts.Add("optional");
        }

        return string.Join(" · ", parts);
    }

    private static string Notes(UsenetServer existing, SabnzbdServer server) =>
        existing.Notes.Length > 0 && server.Notes.Trim().Length == 0 ? existing.Notes : server.Notes.Trim();

    private static string UniqueName(IReadOnlyList<UsenetServer> existing, string name)
    {
        var candidate = name.Length > 0 ? name : "Usenet server";
        if (existing.All(s => !string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return candidate;
        }

        for (var i = 1; ; i++)
        {
            var next = i == 1 ? $"{candidate} (SABnzbd)" : string.Create(CultureInfo.InvariantCulture, $"{candidate} (SABnzbd {i})");
            if (existing.All(s => !string.Equals(s.Name, next, StringComparison.OrdinalIgnoreCase)))
            {
                return next;
            }
        }
    }
}
