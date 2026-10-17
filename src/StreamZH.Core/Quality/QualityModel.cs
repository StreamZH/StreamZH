// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

namespace StreamZH.Core.Quality;

/// <summary>Release revision: v2/v3 and PROPER/REPACK raise <see cref="Version"/>, REAL raises <see cref="Real"/>.</summary>
public sealed record Revision(int Version = 1, int Real = 0, bool IsRepack = false) : IComparable<Revision>
{
    public int CompareTo(Revision? other)
    {
        if (other is null)
        {
            return 1;
        }

        var real = Real.CompareTo(other.Real);
        return real != 0 ? real : Version.CompareTo(other.Version);
    }
}

/// <summary>Quality plus revision of one release or file.</summary>
public sealed record QualityModel(Quality Quality, Revision Revision)
{
    public QualityModel(Quality quality)
        : this(quality, new Revision())
    {
    }

    public static QualityModel Unknown { get; } = new(Quality.Unknown);

    public override string ToString() =>
        Revision.Version > 1 || Revision.Real > 0
            ? $"{Quality} v{Revision.Version}{(Revision.Real > 0 ? $" REAL{Revision.Real}" : string.Empty)}"
            : Quality.ToString();
}

/// <summary>HDR format found in the release name.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HdrFormat>))]
public enum HdrFormat
{
    None = 0,
    Hdr,
    Hdr10,
    Hdr10Plus,
    DolbyVision,
    DolbyVisionHdr10,
    Hlg,
}