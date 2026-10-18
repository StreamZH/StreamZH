// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using StreamZH.Data;
using StreamZH.Library.Search;
using StreamZH.Library.Wanted;
using StreamZH.Tests.Library.Search;

namespace StreamZH.Tests.Library.Wanted;

/// <summary>Wanted and Calendar services on top of the search test bed (fake clock: 2026-10-17 12:00 UTC).</summary>
internal static class WantedTestSupport
{
    public static WantedQueries Queries(this SearchTestBed bed) => new(bed.Database);

    public static LastSearchRepository LastSearches(this SearchTestBed bed) => new(bed.Database);

    public static MediaFileDetailsRepository FileDetails(this SearchTestBed bed) => new(bed.Database);

    public static PendingImports PendingImports(this SearchTestBed bed) =>
        new(new DownloadRepository(bed.Database), new DownloadImportRepository(bed.Database));

    public static QueueSnapshotFactory QueueSnapshots(this SearchTestBed bed) =>
        new(bed.Downloads, bed.Queries(), bed.Engine, bed.PendingImports(), bed.Time);

    public static WantedService Wanted(this SearchTestBed bed) =>
        new(bed.Movies, bed.Series, bed.Profiles, bed.Queries(), bed.LastSearches(),
            new DecisionTargetFactory(bed.Profiles, null, bed.FileDetails(), customFormats: new CustomFormatRepository(bed.Database)), bed.QueueSnapshots(), bed.Events, bed.Options, bed.Time);

    public static CalendarService Calendar(this SearchTestBed bed) =>
        new(bed.Movies, bed.Series, bed.Queries(), bed.QueueSnapshots(), bed.Options, bed.Time);

    /// <summary>The automatic search with the "last searched" times recorded.</summary>
    public static AutomaticSearchService AutomaticWithLastSearch(this SearchTestBed bed) =>
        new(bed.Search(), bed.Grab(), bed.Movies, bed.Series, bed.History, bed.Downloads, bed.Events, NullLogger<AutomaticSearchService>.Instance,
            bed.Engine, bed.Time, bed.Options, bed.PendingImports(), bed.LastSearches());
}
