// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Decisions;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.SceneNumbering;

namespace StreamZH.Library.Search;

public static class EpisodeMapperExtensions
{
    /// <summary>The mapper's answer, or the plain mapping (series type and scene numbers, no scene names) without one (tests).</summary>
    public static (IReadOnlyList<Episode> Episodes, Rejection? Rejection) MapEpisodes(
        this EpisodeMapper? mapper, ParsedRelease parsed, Series series, IReadOnlyList<Episode> library, bool sceneSource = true) =>
        mapper is null ? ReleaseMatching.MapEpisodes(parsed, library, series, sceneSource) : mapper.Map(parsed, series, library, sceneSource);
}
