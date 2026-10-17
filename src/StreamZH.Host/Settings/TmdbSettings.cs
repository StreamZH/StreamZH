// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Data;
using StreamZH.Metadata.Tmdb;

namespace StreamZH.Host.Settings;

public sealed class TmdbSettings(ConfigRepository config) : ITmdbSettings
{
    public const string DefaultLanguage = "en-US";

    public static readonly string[] Languages = ["en-US", "de-DE", "de-CH", "fr-FR", "it-IT", "es-ES", "pt-BR", "nl-NL"];

    public string? Token => config.Get(ConfigKeys.TmdbToken);

    /// <summary>
    /// Language sent to TMDB. Regional variants with few translations (de-CH, de-AT, fr-CH, it-CH) are sent as the
    /// main variant, because TMDB falls back to the original title instead of e.g. the German one.
    /// </summary>
    public string Language => ForTmdb(config.Get(ConfigKeys.TmdbLanguage) ?? DefaultLanguage);

    public static string ForTmdb(string language) => language switch
    {
        "de-CH" or "de-AT" => "de-DE",
        "fr-CH" => "fr-FR",
        "it-CH" => "it-IT",
        _ => language,
    };
}