// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr), Copyright (c) Radarr contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace StreamZH.Core.Quality;

/// <summary>Where a release comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualitySource>))]
public enum QualitySource
{
    Unknown = 0,
    Cam,
    Telesync,
    Telecine,
    Workprint,
    Dvd,
    Tv,
    WebDl,
    WebRip,
    Bluray,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityModifier>))]
public enum QualityModifier
{
    None = 0,
    Regional,
    Screener,
    RawHd,
    BrDisk,
    Remux,
}

/// <summary>
/// A quality like "WEBDL-1080p". Ids are the same as in Radarr, so profiles and data can be imported later.
/// Weight orders qualities from worst to best; qualities with the same weight are equal (e.g. WEBDL and WEBRip).
/// </summary>
public sealed class Quality : IEquatable<Quality>
{
    private Quality(int id, string name, QualitySource source, int resolution, int weight, QualityModifier modifier = QualityModifier.None)
    {
        Id = id;
        Name = name;
        Source = source;
        Resolution = resolution;
        Weight = weight;
        Modifier = modifier;
    }

    public int Id { get; }
    public string Name { get; }
    public QualitySource Source { get; }
    public int Resolution { get; }
    public QualityModifier Modifier { get; }
    public int Weight { get; }

    public static Quality Unknown { get; } = new(0, "Unknown", QualitySource.Unknown, 0, 1);
    public static Quality Workprint { get; } = new(24, "WORKPRINT", QualitySource.Workprint, 0, 2);
    public static Quality Cam { get; } = new(25, "CAM", QualitySource.Cam, 0, 3);
    public static Quality Telesync { get; } = new(26, "TELESYNC", QualitySource.Telesync, 0, 4);
    public static Quality Telecine { get; } = new(27, "TELECINE", QualitySource.Telecine, 0, 5);
    public static Quality Regional { get; } = new(29, "REGIONAL", QualitySource.Dvd, 480, 6, QualityModifier.Regional);
    public static Quality DvdScr { get; } = new(28, "DVDSCR", QualitySource.Dvd, 480, 7, QualityModifier.Screener);
    public static Quality Sdtv { get; } = new(1, "SDTV", QualitySource.Tv, 480, 8);
    public static Quality Dvd { get; } = new(2, "DVD", QualitySource.Dvd, 0, 9);
    public static Quality DvdR { get; } = new(23, "DVD-R", QualitySource.Dvd, 480, 10, QualityModifier.Remux);
    public static Quality WebDl480p { get; } = new(8, "WEBDL-480p", QualitySource.WebDl, 480, 11);
    public static Quality WebRip480p { get; } = new(12, "WEBRip-480p", QualitySource.WebRip, 480, 11);
    public static Quality Bluray480p { get; } = new(20, "Bluray-480p", QualitySource.Bluray, 480, 12);
    public static Quality Bluray576p { get; } = new(21, "Bluray-576p", QualitySource.Bluray, 576, 13);
    public static Quality Hdtv720p { get; } = new(4, "HDTV-720p", QualitySource.Tv, 720, 14);
    public static Quality WebDl720p { get; } = new(5, "WEBDL-720p", QualitySource.WebDl, 720, 15);
    public static Quality WebRip720p { get; } = new(14, "WEBRip-720p", QualitySource.WebRip, 720, 15);
    public static Quality Bluray720p { get; } = new(6, "Bluray-720p", QualitySource.Bluray, 720, 16);
    public static Quality Hdtv1080p { get; } = new(9, "HDTV-1080p", QualitySource.Tv, 1080, 17);
    public static Quality WebDl1080p { get; } = new(3, "WEBDL-1080p", QualitySource.WebDl, 1080, 18);
    public static Quality WebRip1080p { get; } = new(15, "WEBRip-1080p", QualitySource.WebRip, 1080, 18);
    public static Quality Bluray1080p { get; } = new(7, "Bluray-1080p", QualitySource.Bluray, 1080, 19);
    public static Quality Remux1080p { get; } = new(30, "Remux-1080p", QualitySource.Bluray, 1080, 20, QualityModifier.Remux);
    public static Quality Hdtv2160p { get; } = new(16, "HDTV-2160p", QualitySource.Tv, 2160, 21);
    public static Quality WebDl2160p { get; } = new(18, "WEBDL-2160p", QualitySource.WebDl, 2160, 22);
    public static Quality WebRip2160p { get; } = new(17, "WEBRip-2160p", QualitySource.WebRip, 2160, 22);
    public static Quality Bluray2160p { get; } = new(19, "Bluray-2160p", QualitySource.Bluray, 2160, 23);
    public static Quality Remux2160p { get; } = new(31, "Remux-2160p", QualitySource.Bluray, 2160, 24, QualityModifier.Remux);
    public static Quality BrDisk { get; } = new(22, "BR-DISK", QualitySource.Bluray, 1080, 25, QualityModifier.BrDisk);
    public static Quality RawHd { get; } = new(10, "Raw-HD", QualitySource.Tv, 1080, 26, QualityModifier.RawHd);

    /// <summary>All qualities from worst to best.</summary>
    public static IReadOnlyList<Quality> All { get; } =
    [
        Unknown, Workprint, Cam, Telesync, Telecine, Regional, DvdScr, Sdtv, Dvd, DvdR,
        WebDl480p, WebRip480p, Bluray480p, Bluray576p,
        Hdtv720p, WebDl720p, WebRip720p, Bluray720p,
        Hdtv1080p, WebDl1080p, WebRip1080p, Bluray1080p, Remux1080p,
        Hdtv2160p, WebDl2160p, WebRip2160p, Bluray2160p, Remux2160p,
        BrDisk, RawHd,
    ];

    private static readonly Dictionary<int, Quality> ById = All.ToDictionary(q => q.Id);

    public static Quality FindById(int id) => ById.TryGetValue(id, out var quality) ? quality : Unknown;

    /// <summary>Find by source, resolution and modifier, e.g. (WebDl, 1080, None) → WEBDL-1080p. Unknown if none matches.</summary>
    public static Quality Find(QualitySource source, int resolution, QualityModifier modifier) =>
        All.FirstOrDefault(q => q.Source == source && q.Resolution == resolution && q.Modifier == modifier) ?? Unknown;

    public bool Equals(Quality? other) => other is not null && other.Id == Id;

    public override bool Equals(object? obj) => Equals(obj as Quality);

    public override int GetHashCode() => Id;

    public override string ToString() => Name;

    public static bool operator ==(Quality? left, Quality? right) => Equals(left, right);

    public static bool operator !=(Quality? left, Quality? right) => !Equals(left, right);
}