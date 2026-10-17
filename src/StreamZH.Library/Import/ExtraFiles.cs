// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions based on Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using StreamZH.Core.Quality;

namespace StreamZH.Library.Import;

/// <summary>
/// Extra files next to a video (subtitles, .nfo): which ones belong to a video and what they are called after the import.
/// Subtitles keep their language and flags ("movie.de.forced.srt" → "&lt;new name&gt;.de.forced.srt"), like Radarr's
/// ExistingExtraFileImporter; a scene .nfo becomes ".nfo-orig" so media centers do not mistake it for their metadata.
/// </summary>
public static partial class ExtraFiles
{
    private static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "sdh", "cc", "hi", "default", "foreign",
    };

    /// <summary>Extra files in <paramref name="folder"/> (and its subfolders, e.g. "Subs") with one of the extensions.</summary>
    public static IReadOnlyList<string> Find(string folder, IReadOnlySet<string> extensions)
    {
        if (extensions.Count == 0 || !Directory.Exists(folder))
        {
            return [];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return [.. Directory.EnumerateFiles(folder, "*", options)
            .Where(f => extensions.Contains(Path.GetExtension(f).TrimStart('.').ToLowerInvariant()))
            .Where(f => !Path.GetFileName(f).Contains("sample", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The new name of an extra file for a video named <paramref name="newBaseName"/> (without extension).
    /// <paramref name="oldVideoBaseName"/> is the video's original name: what follows it in the extra's name is kept.
    /// </summary>
    public static string TargetName(string extraPath, string oldVideoBaseName, string newBaseName)
    {
        var extension = Path.GetExtension(extraPath).ToLowerInvariant();
        if (extension == ".nfo")
        {
            return newBaseName + ".nfo-orig";
        }

        var name = Path.GetFileNameWithoutExtension(extraPath);
        string suffix;
        if (name.StartsWith(oldVideoBaseName + ".", StringComparison.OrdinalIgnoreCase))
        {
            suffix = name[oldVideoBaseName.Length..];
        }
        else
        {
            suffix = string.Concat(LanguageAndFlags(name).Select(p => "." + p));
        }

        return newBaseName + suffix + extension;
    }

    /// <summary>Language codes and flags at the end of a subtitle name: "Movie.German.forced" → ["de", "forced"], "2_English" → ["en"].</summary>
    public static IReadOnlyList<string> LanguageAndFlags(string name)
    {
        var parts = SplitRegex().Split(name).Where(p => p.Length > 0).ToList();
        var result = new List<string>();
        for (var i = parts.Count - 1; i >= 0 && i >= parts.Count - 3; i--)
        {
            var part = parts[i];
            if (Flags.Contains(part))
            {
                result.Insert(0, part.ToLowerInvariant());
            }
            else if (LanguageCode(part) is { } code)
            {
                result.Insert(0, code);
                break;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static string? LanguageCode(string part)
    {
        if (part.Length is 2 or 3 && part.All(char.IsLetter))
        {
            var lower = part.ToLowerInvariant();
            if (Language.FromIsoCode(lower) != Language.Unknown)
            {
                return lower;
            }

            return lower switch
            {
                "eng" => "en",
                "ger" or "deu" => "de",
                "fre" or "fra" => "fr",
                "spa" => "es",
                "ita" => "it",
                "dut" or "nld" => "nl",
                "jpn" => "ja",
                "rus" => "ru",
                "por" => "pt",
                "swe" => "sv",
                "nor" => "no",
                "dan" => "da",
                "fin" => "fi",
                "pol" => "pl",
                "tur" => "tr",
                "kor" => "ko",
                "chi" or "zho" => "zh",
                _ => null,
            };
        }

        var named = Language.All.FirstOrDefault(l => l.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
        return named switch
        {
            null => null,
            _ when named == Language.English => "en",
            _ when named == Language.German => "de",
            _ when named == Language.French => "fr",
            _ when named == Language.Spanish => "es",
            _ when named == Language.Italian => "it",
            _ when named == Language.Dutch => "nl",
            _ when named == Language.Japanese => "ja",
            _ => named.Name.ToLowerInvariant(),
        };
    }

    [GeneratedRegex(@"[._\s-]+")]
    private static partial Regex SplitRegex();
}
