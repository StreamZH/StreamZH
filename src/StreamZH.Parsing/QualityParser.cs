// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Parsing;

/// <summary>Reads quality, revision and HDR format from a release or file name.</summary>
public static class QualityParser
{
    public static QualityModel ParseQuality(string name) => QualityModel.Unknown;

    public static HdrFormat ParseHdr(string name) => HdrFormat.None;
}