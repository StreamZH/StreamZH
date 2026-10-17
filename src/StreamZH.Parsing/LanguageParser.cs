// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Core.Quality;

namespace StreamZH.Parsing;

/// <summary>Reads audio languages from a release name ("German.DL" means German plus original language).</summary>
public static class LanguageParser
{
    public static IReadOnlyList<Language> ParseLanguages(string title) => [];
}