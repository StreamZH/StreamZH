// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using StreamZH.Data;
using StreamZH.Library.ImportLists;

namespace StreamZH.Host.Status;

/// <summary>
/// Status-window notices of Settings → Import lists: one warning per enabled list that failed
/// <see cref="FailuresForNotice"/> syncs in a row ("Plex watchlist: rejected the credentials"). It disappears with the
/// next successful sync, or when the list is saved with other settings or switched off.
/// </summary>
public static class ImportListNotices
{
    public const string KeyPrefix = "import-list-failed-";

    /// <summary>One failure can be a short outage; two in a row (two days with the default interval) need a look.</summary>
    public const int FailuresForNotice = 2;

    private const string Link = "/settings/import-lists";

    public static void Add(List<OngoingNotice> notices, ImportListRepository? repository, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(time);
        if (repository is null)
        {
            return;
        }

        IReadOnlyList<ImportListDefinition> lists;
        try
        {
            lists = repository.All();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return; // Table not there yet (a database before 0.7 while migrating).
        }

        foreach (var list in lists.Where(l => l.Enabled && l.Status.Failures >= FailuresForNotice))
        {
            var type = ImportListCatalog.Find(list.Implementation)?.Name ?? list.Implementation;
            var since = list.Status.LastSuccess is { } ok
                ? $"last success {TimeZoneInfo.ConvertTime(new DateTimeOffset(ok, TimeSpan.Zero), time.LocalTimeZone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}"
                : "never synced";
            notices.Add(new OngoingNotice(
                $"{KeyPrefix}{list.Id.ToString(CultureInfo.InvariantCulture)}",
                NoticeLevels.Warn,
                $"{list.Name}: {list.Status.LastError ?? "sync failed"}",
                $"{type} · failed {list.Status.Failures} times in a row · {since}",
                Link,
                false));
        }
    }
}
