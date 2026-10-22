// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Quality;

/// <summary>Audio language of a release. Ids are the same as in Radarr.</summary>
public sealed class Language : IEquatable<Language>
{
    private Language(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string Name { get; }

    public static Language Unknown { get; } = new(0, "Unknown");
    public static Language English { get; } = new(1, "English");
    public static Language French { get; } = new(2, "French");
    public static Language Spanish { get; } = new(3, "Spanish");
    public static Language German { get; } = new(4, "German");
    public static Language Italian { get; } = new(5, "Italian");
    public static Language Danish { get; } = new(6, "Danish");
    public static Language Dutch { get; } = new(7, "Dutch");
    public static Language Japanese { get; } = new(8, "Japanese");
    public static Language Icelandic { get; } = new(9, "Icelandic");
    public static Language Chinese { get; } = new(10, "Chinese");
    public static Language Russian { get; } = new(11, "Russian");
    public static Language Polish { get; } = new(12, "Polish");
    public static Language Vietnamese { get; } = new(13, "Vietnamese");
    public static Language Swedish { get; } = new(14, "Swedish");
    public static Language Norwegian { get; } = new(15, "Norwegian");
    public static Language Finnish { get; } = new(16, "Finnish");
    public static Language Turkish { get; } = new(17, "Turkish");
    public static Language Portuguese { get; } = new(18, "Portuguese");
    public static Language Flemish { get; } = new(19, "Flemish");
    public static Language Greek { get; } = new(20, "Greek");
    public static Language Korean { get; } = new(21, "Korean");
    public static Language Hungarian { get; } = new(22, "Hungarian");
    public static Language Hebrew { get; } = new(23, "Hebrew");
    public static Language Lithuanian { get; } = new(24, "Lithuanian");
    public static Language Czech { get; } = new(25, "Czech");
    public static Language Hindi { get; } = new(26, "Hindi");
    public static Language Romanian { get; } = new(27, "Romanian");
    public static Language Thai { get; } = new(28, "Thai");
    public static Language Bulgarian { get; } = new(29, "Bulgarian");
    public static Language PortugueseBrazil { get; } = new(30, "Portuguese (Brazil)");
    public static Language Arabic { get; } = new(31, "Arabic");
    public static Language Ukrainian { get; } = new(32, "Ukrainian");
    public static Language Persian { get; } = new(33, "Persian");
    public static Language Bengali { get; } = new(34, "Bengali");
    public static Language Slovak { get; } = new(35, "Slovak");
    public static Language Latvian { get; } = new(36, "Latvian");
    public static Language SpanishLatino { get; } = new(37, "Spanish (Latino)");
    public static Language Catalan { get; } = new(38, "Catalan");
    public static Language Croatian { get; } = new(39, "Croatian");
    public static Language Serbian { get; } = new(40, "Serbian");
    public static Language Bosnian { get; } = new(41, "Bosnian");
    public static Language Estonian { get; } = new(42, "Estonian");
    public static Language Tamil { get; } = new(43, "Tamil");
    public static Language Indonesian { get; } = new(44, "Indonesian");
    public static Language Telugu { get; } = new(45, "Telugu");
    public static Language Macedonian { get; } = new(46, "Macedonian");
    public static Language Slovenian { get; } = new(47, "Slovenian");
    public static Language Malayalam { get; } = new(48, "Malayalam");
    public static Language Kannada { get; } = new(49, "Kannada");
    public static Language Albanian { get; } = new(50, "Albanian");
    public static Language Afrikaans { get; } = new(51, "Afrikaans");
    public static Language Marathi { get; } = new(52, "Marathi");
    public static Language Tagalog { get; } = new(53, "Tagalog");
    public static Language Urdu { get; } = new(54, "Urdu");
    public static Language Romansh { get; } = new(55, "Romansh");
    public static Language Mongolian { get; } = new(56, "Mongolian");
    public static Language Georgian { get; } = new(57, "Georgian");

    /// <summary>Profile setting: any language is fine.</summary>
    public static Language Any { get; } = new(-1, "Any");

    /// <summary>Profile setting: the original language of the movie or series.</summary>
    public static Language Original { get; } = new(-2, "Original");

    /// <summary>Real languages (without Unknown, Any, Original), alphabetically.</summary>
    public static IReadOnlyList<Language> All { get; } = typeof(Language)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(Language))
        .Select(p => (Language)p.GetValue(null)!)
        .Where(l => l.Id > 0)
        .OrderBy(l => l.Name, StringComparer.Ordinal)
        .ToList();

    private static readonly Dictionary<int, Language> ById =
        new[] { Unknown, Any, Original }.Concat(All).ToDictionary(l => l.Id);

    public static Language FindById(int id) => ById.TryGetValue(id, out var language) ? language : Unknown;

    // ISO 639-1 codes as TMDB and browsers deliver them; the first code of a language is its own (IsoCode).
    // "cn" is TMDB's code for Cantonese, filed under Chinese.
    private static readonly (string Code, Language Language)[] IsoTable =
    [
        ("en", English), ("fr", French), ("es", Spanish), ("de", German), ("it", Italian), ("da", Danish),
        ("nl", Dutch), ("ja", Japanese), ("is", Icelandic), ("zh", Chinese), ("cn", Chinese), ("ru", Russian),
        ("pl", Polish), ("vi", Vietnamese), ("sv", Swedish), ("no", Norwegian), ("nb", Norwegian), ("nn", Norwegian),
        ("fi", Finnish), ("tr", Turkish), ("pt", Portuguese), ("el", Greek), ("ko", Korean), ("hu", Hungarian),
        ("he", Hebrew), ("lt", Lithuanian), ("cs", Czech), ("hi", Hindi), ("ro", Romanian), ("th", Thai),
        ("bg", Bulgarian), ("ar", Arabic), ("uk", Ukrainian), ("fa", Persian), ("bn", Bengali), ("sk", Slovak),
        ("lv", Latvian), ("ca", Catalan), ("hr", Croatian), ("sr", Serbian), ("bs", Bosnian), ("et", Estonian),
        ("ta", Tamil), ("id", Indonesian), ("te", Telugu), ("mk", Macedonian), ("sl", Slovenian), ("ml", Malayalam),
        ("kn", Kannada), ("sq", Albanian), ("af", Afrikaans), ("mr", Marathi), ("tl", Tagalog), ("ur", Urdu),
        ("rm", Romansh), ("mn", Mongolian), ("ka", Georgian),
    ];

    private static readonly Dictionary<string, Language> ByIso =
        IsoTable.ToDictionary(e => e.Code, e => e.Language, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, string> IsoById = IsoTable
        .GroupBy(e => e.Language.Id)
        .ToDictionary(g => g.Key, g => g.First().Code);

    /// <summary>Language from an ISO 639-1 code as TMDB delivers it ("en", "de"); Unknown if not mapped.</summary>
    public static Language FromIsoCode(string? code) =>
        code is not null && ByIso.TryGetValue(code.Trim(), out var language) ? language : Unknown;

    /// <summary>
    /// Language of a BCP 47 language tag as browsers send it ("tr-TR", "de-CH", "fr"): its primary subtag (ISO 639-1);
    /// Unknown if not mapped.
    /// </summary>
    public static Language FromLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Unknown;
        }

        var primary = tag.Trim().Split('-', '_')[0];
        return primary.Length is 2 or 3 ? FromIsoCode(primary) : Unknown;
    }

    /// <summary>The ISO 639-1 code of this language ("tr" for Turkish); null for languages without one.</summary>
    public string? IsoCode => IsoById.TryGetValue(Id, out var code) ? code : null;

    public bool Equals(Language? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => Equals(obj as Language);

    public override int GetHashCode() => Id;

    public override string ToString() => Name;

    public static bool operator ==(Language? left, Language? right) => Equals(left, right);

    public static bool operator !=(Language? left, Language? right) => !Equals(left, right);
}