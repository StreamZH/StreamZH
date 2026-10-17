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

    public string Language => config.Get(ConfigKeys.TmdbLanguage) ?? DefaultLanguage;
}