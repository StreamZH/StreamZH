// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;

namespace StreamZH.Host.Status;

/// <summary>
/// 0.12: one notice per stored secret that cannot be decrypted (the key folder is missing or belongs to another
/// installation): "secret unreadable — enter it again", with a link to the page where it is entered. It disappears once
/// the item is saved with the secret.
/// </summary>
public static class SecretNotices
{
    public static void Add(List<OngoingNotice> notices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<Database>() is not { } database)
        {
            return;
        }

        foreach (var group in database.Secrets.Unreadable.GroupBy(l => (l.Kind, l.Item)))
        {
            var (kind, item) = group.Key;
            var (title, link) = Describe(kind, item);
            var fields = string.Join(", ", group.Select(l => l.Field).Distinct());
            notices.Add(new OngoingNotice(
                $"secret-unreadable-{kind}-{item}".ToLowerInvariant().Replace(' ', '-'),
                NoticeLevels.Warn,
                $"{title}: secret unreadable",
                kind == ConfigRepository.SettingKind && item == ConfigKeys.ApiKey
                    ? "It was saved with keys this installation does not have. Create a new key."
                    : kind == ConfigRepository.SettingKind
                    ? "It was saved with keys this installation does not have. Enter it again."
                    : $"{fields}: saved with keys this installation does not have. Enter it again.",
                link,
                false));
        }
    }

    private static (string Title, string Link) Describe(string kind, string item) => kind switch
    {
        IndexerRepository.SecretKind => ($"Indexer {item}", "/indexers"),
        UsenetServerRepository.SecretKind => ($"Usenet server {item}", "/settings/usenet"),
        DownloadClientRepository.SecretKind => ($"Download client {item}", "/settings/downloadclients"),
        ConnectionRepository.SecretKind => ($"Connection {item}", "/settings/connect"),
        ImportListRepository.SecretKind => ($"Import list {item}", "/settings/import-lists"),
        _ => item switch
        {
            ConfigKeys.ApiKey => ("API key", "/settings/general#api-key"),
            ConfigKeys.ProxyPassword => ("Proxy password", "/settings/general#proxy"),
            ConfigKeys.TmdbToken => ("TMDB token", "/settings/metadata"),
            ConfigKeys.DiscordBotToken => ("Discord bot token", "/settings/discord"),
            _ => (item, "/settings"),
        },
    };
}
