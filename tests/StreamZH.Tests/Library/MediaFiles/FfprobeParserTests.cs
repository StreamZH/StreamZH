// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using StreamZH.Core.Media;
using StreamZH.Core.Quality;
using StreamZH.Library.MediaFiles;

namespace StreamZH.Tests.Library.MediaFiles;

/// <summary>
/// ffprobe JSON as ffprobe 6.1 prints it for real releases (generated from sample files, then given the tags, side data
/// and Atmos profiles of real Blu-ray remuxes and anime releases).
/// </summary>
public sealed class FfprobeParserTests
{
    public const string DvRelease = "Dune.Part.Two.2024.German.DL.2160p.UHD.BluRay.DV.HDR10.HEVC.TrueHD.Atmos-GRP.mkv";
    public const string X264Release = "Das.Boot.1981.German.DL.1080p.BluRay.x264-DETAiLS.mkv";
    public const string AnimeRelease = "[GRP] Frieren - Beyond Journey's End - S01E01 [German Dub] [1080p] [HEVC 10bit] [Dual Audio].mkv";

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Library", "MediaFiles", "Files", name));

    public static MediaInfo DolbyVision() => FfprobeParser.Parse(Fixture("dv-hdr10-truehd-atmos.json"), Fixture("dv-hdr10-truehd-atmos.frames.json"), DvRelease);

    public static MediaInfo X264() => FfprobeParser.Parse(Fixture("x264-ac3-eac3.json"), null, X264Release);

    public static MediaInfo Anime() => FfprobeParser.Parse(Fixture("anime-jpn-ger-subs.json"), null, AnimeRelease);

    [Fact]
    public void Dolby_vision_remux_with_truehd_atmos_in_german_and_english()
    {
        var info = DolbyVision();

        Assert.Equal("Matroska", info.Container);
        Assert.Equal(9965.123, info.RuntimeSeconds);
        Assert.Equal(55167291, info.OverallBitrate);
        Assert.Equal(68719476736, info.FileSize);

        var video = info.Video!;
        Assert.Equal("hevc", video.CodecName);
        Assert.Equal("HEVC", video.Codec); // the release name says HEVC (Radarr's scene name match)
        Assert.Equal("Main 10", video.Profile);
        Assert.Equal(10, video.BitDepth);
        Assert.Equal((3840, 2160, 2160), (video.Width, video.Height, video.Resolution));
        Assert.Equal(23.976, video.FrameRate);
        Assert.Equal("Progressive", video.ScanType);
        Assert.Equal(MediaHdrType.DolbyVisionHdr10, video.Hdr);
        Assert.Equal("DV HDR10", video.DynamicRangeType);
        Assert.Equal("HDR", video.DynamicRange);
        Assert.Equal(8, video.DolbyVisionProfile);
        Assert.Equal(50184316, video.Bitrate); // from mkvmerge's BPS tag
        Assert.False(video.Is3D);

        Assert.Equal(3, info.AudioTracks.Count);
        var german = info.AudioTracks[0];
        Assert.Equal(("TrueHD Atmos", 7.1m, "7.1", "ger", Language.German.Id, true, false), (german.Codec, german.Channels, german.ChannelsText, german.LanguageCode, german.LanguageId, german.Default, german.Commentary));
        Assert.Equal(4524115, german.Bitrate);
        Assert.Equal(("TrueHD Atmos", Language.English.Id), (info.AudioTracks[1].Codec, info.AudioTracks[1].LanguageId));
        var commentary = info.AudioTracks[2];
        Assert.Equal(("AC3", 2.0m, true), (commentary.Codec, commentary.Channels, commentary.Commentary));
        Assert.Same(german, info.PrimaryAudio);
        Assert.Equal([Language.German, Language.English], info.AudioLanguages);

        Assert.Equal(3, info.Subtitles.Count);
        Assert.All(info.Subtitles, s => Assert.Equal("PGS", s.Format));
        Assert.True(info.Subtitles[0].Forced);
        Assert.True(info.Subtitles[2].HearingImpaired);
        Assert.Equal([Language.German, Language.English], info.SubtitleLanguages);
    }

    [Fact]
    public void X264_with_ac3_german_and_eac3_english()
    {
        var info = X264();

        var video = info.Video!;
        Assert.Equal(("h264", "x264", "High", 8), (video.CodecName, video.Codec, video.Profile, video.BitDepth));
        Assert.Equal((1920, 800, 1080), (video.Width, video.Height, video.Resolution)); // scope: the width decides
        Assert.Equal(MediaHdrType.None, video.Hdr);
        Assert.Equal(string.Empty, video.DynamicRangeType);

        Assert.Equal(["AC3", "EAC3"], info.AudioTracks.Select(a => a.Codec));
        Assert.All(info.AudioTracks, a => Assert.Equal("5.1", a.ChannelsText));
        Assert.Equal(640000, info.AudioTracks[0].Bitrate); // BPS tag
        Assert.Equal(Language.German.Id, info.PrimaryAudio!.LanguageId);
        Assert.Equal([Language.German, Language.English], info.AudioLanguages);

        Assert.Equal(["SRT", "SRT"], info.Subtitles.Select(s => s.Format));
        Assert.True(info.Subtitles[0].Forced);
        Assert.False(info.Subtitles[1].Forced);
    }

    [Fact]
    public void Anime_with_japanese_and_german_audio_and_subtitles()
    {
        var info = Anime();

        var video = info.Video!;
        Assert.Equal(("hevc", "HEVC", 10, 1080), (video.CodecName, video.Codec, video.BitDepth, video.Resolution));
        Assert.Equal(MediaHdrType.None, video.Hdr); // 10-bit, but BT.709

        Assert.Equal(["FLAC", "AAC"], info.AudioTracks.Select(a => a.Codec));
        Assert.Equal([2.0m, 2.0m], info.AudioTracks.Select(a => a.Channels));
        Assert.Equal(Language.Japanese.Id, info.PrimaryAudio!.LanguageId);
        Assert.Equal([Language.Japanese, Language.German], info.AudioLanguages);

        // The attached font is no subtitle.
        Assert.Equal(["ASS", "ASS", "ASS"], info.Subtitles.Select(s => s.Format));
        Assert.Equal([Language.German, Language.English], info.SubtitleLanguages);
        Assert.True(info.Subtitles[2].Forced); // signs & songs
        Assert.Equal("Deutsch [Untertitel]", info.Subtitles[0].Title);
    }

    [Fact]
    public void Video_codec_without_a_scene_name_is_h264_or_from_the_encoder_tag()
    {
        Assert.Equal("h264", FfprobeParser.Parse(Fixture("x264-ac3-eac3.json")).Video!.Codec);
        Assert.Equal("h265", FfprobeParser.Parse(Fixture("anime-jpn-ger-subs.json"), null, "frieren.s01e01.mkv").Video!.Codec);
        Assert.Equal("x264", FfprobeParser.VideoCodec("h264", null, "Lavc60.31.102 libx264", null));
        Assert.Equal("x265", FfprobeParser.VideoCodec("hevc", null, null, "Show.S01E01.1080p.WEB.H.265-GRP x265"));
        Assert.Equal("h264", FfprobeParser.VideoCodec("h264", null, null, "Show.S01E01.1080p.WEB.H.264-GRP"));
        Assert.Equal("AVC", FfprobeParser.VideoCodec("h264", null, null, "Movie.2010.1080p.BluRay.REMUX.AVC.DTS-HD.MA.5.1-GRP"));
        Assert.Equal("AV1", FfprobeParser.VideoCodec("av1", null, null, null));
        Assert.Equal("VC1", FfprobeParser.VideoCodec("vc1", null, null, null));
        Assert.Equal("MPEG2", FfprobeParser.VideoCodec("mpeg2video", null, null, null));
        Assert.Equal("XviD", FfprobeParser.VideoCodec("mpeg4", "XVID", null, null));
        Assert.Equal("DivX", FfprobeParser.VideoCodec("mpeg4", "DX50", null, null));
    }

    [Theory]
    [InlineData("truehd", null, null, "TrueHD")]
    [InlineData("truehd", null, "Dolby TrueHD + Dolby Atmos", "TrueHD Atmos")]
    [InlineData("eac3", null, null, "EAC3")]
    [InlineData("eac3", null, "Dolby Digital Plus + Dolby Atmos", "EAC3 Atmos")]
    [InlineData("eac3", "ec+3", null, "EAC3 Atmos")]
    [InlineData("ac3", null, null, "AC3")]
    [InlineData("dts", null, "DTS", "DTS")]
    [InlineData("dts", null, "DTS-HD MA", "DTS-HD MA")]
    [InlineData("dts", null, "DTS-HD MA + DTS:X", "DTS-X")]
    [InlineData("dts", null, "DTS-HD MA + DTS:X IMAX", "DTS-X")]
    [InlineData("dts", null, "DTS-HD HRA", "DTS-HD HRA")]
    [InlineData("dts", null, "DTS-ES", "DTS-ES")]
    [InlineData("aac", null, "LC", "AAC")]
    [InlineData("aac", null, "HE-AAC", "HE-AAC")]
    [InlineData("flac", null, null, "FLAC")]
    [InlineData("opus", null, null, "Opus")]
    [InlineData("mp3", null, null, "MP3")]
    [InlineData("pcm_s24le", null, null, "PCM")]
    [InlineData("pcm_bluray", null, null, "PCM")]
    public void Audio_codecs_are_named_like_radarr(string codec, string? tag, string? profile, string expected) =>
        Assert.Equal(expected, FfprobeParser.AudioCodec(codec, tag, profile));

    [Theory]
    [InlineData("mono", 1, 1.0)]
    [InlineData("stereo", 2, 2.0)]
    [InlineData("2.1", 3, 2.1)]
    [InlineData("5.1(side)", 6, 5.1)]
    [InlineData("5.1", 6, 5.1)]
    [InlineData("6.1", 7, 6.1)]
    [InlineData("7.1", 8, 7.1)]
    [InlineData("7.1(wide)", 8, 7.1)]
    [InlineData(null, 6, 5.1)]
    [InlineData(null, 8, 7.1)]
    [InlineData("", 2, 2.0)]
    public void Channels_from_the_layout_else_the_count(string? layout, int channels, double expected) =>
        Assert.Equal((decimal)expected, FfprobeParser.Channels(layout, channels));

    [Fact]
    public void Hdr10_hdr10plus_and_pq_come_from_the_first_frame()
    {
        var withoutDovi = WithoutStreamSideData(Fixture("dv-hdr10-truehd-atmos.json"));

        Assert.True(FfprobeParser.NeedsFrameSideData(withoutDovi));
        Assert.Equal(MediaHdrType.Hdr10, FfprobeParser.Parse(withoutDovi, Fixture("dv-hdr10-truehd-atmos.frames.json")).Video!.Hdr);
        Assert.Equal(MediaHdrType.Pq, FfprobeParser.Parse(withoutDovi).Video!.Hdr);
        Assert.Equal(MediaHdrType.Hdr10Plus, FfprobeParser.Parse(withoutDovi, Hdr10PlusFrame).Video!.Hdr);
        Assert.Equal("HDR10Plus", FfprobeParser.Parse(withoutDovi, Hdr10PlusFrame).Video!.DynamicRangeType);
        Assert.Equal(MediaHdrType.DolbyVisionHdr10Plus, FfprobeParser.Parse(Fixture("dv-hdr10-truehd-atmos.json"), Hdr10PlusFrame).Video!.Hdr);
        Assert.False(FfprobeParser.NeedsFrameSideData(Fixture("x264-ac3-eac3.json")));
    }

    [Theory]
    [InlineData(1, "DolbyVisionHdr10")]
    [InlineData(6, "DolbyVisionHdr10")]
    [InlineData(2, "DolbyVisionSdr")]
    [InlineData(4, "DolbyVisionHlg")]
    [InlineData(0, "DolbyVision")] // profile 5: no compatible base layer
    public void Dolby_vision_base_layer_from_the_compatibility_id(int compatibility, string expected)
    {
        var json = JsonNode.Parse(Fixture("dv-hdr10-truehd-atmos.json"))!;
        json["streams"]![0]!["side_data_list"]![0]!["dv_bl_signal_compatibility_id"] = compatibility;

        Assert.Equal(expected, FfprobeParser.Parse(json.ToJsonString()).Video!.Hdr.ToString());
    }

    [Fact]
    public void Hlg_and_sdr()
    {
        Assert.Equal(MediaHdrType.Hlg, FfprobeParser.HdrType(10, "bt2020", "arib-std-b67", []));
        Assert.Equal(MediaHdrType.None, FfprobeParser.HdrType(8, "bt2020", "smpte2084", []));
        Assert.Equal(MediaHdrType.None, FfprobeParser.HdrType(10, "bt709", "bt709", []));
    }

    [Fact]
    public void Undefined_languages_and_attached_pictures_are_ignored()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_name":"mjpeg","codec_type":"video","width":600,"height":900,"disposition":{"attached_pic":1}},
              {"index":1,"codec_name":"h264","codec_type":"video","width":1280,"height":720,"pix_fmt":"yuv420p","field_order":"tt","avg_frame_rate":"25/1"},
              {"index":2,"codec_name":"aac","codec_type":"audio","channels":2,"tags":{"language":"und"}},
              {"index":3,"codec_name":"mp3","codec_type":"audio","channels":2,"tags":{"language":"deu"}}],
             "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"60.0"}}
            """;

        var info = FfprobeParser.Parse(json);

        Assert.Equal("MP4", info.Container);
        Assert.Equal((1280, 720, "Interlaced", 25.0), (info.Video!.Width, info.Video.Resolution, info.Video.ScanType, info.Video.FrameRate));
        Assert.Null(info.AudioTracks[0].LanguageCode);
        Assert.Equal(0, info.AudioTracks[0].LanguageId);
        Assert.Equal(Language.German.Id, info.AudioTracks[1].LanguageId);
        Assert.Equal([Language.German], info.AudioLanguages);
    }

    [Theory]
    [InlineData("ger", "German")]
    [InlineData("deu", "German")]
    [InlineData("de", "German")]
    [InlineData("gsw", "German")]
    [InlineData("eng", "English")]
    [InlineData("jpn", "Japanese")]
    [InlineData("fre", "French")]
    [InlineData("fra", "French")]
    [InlineData("chi", "Chinese")]
    [InlineData("pt-BR", "Portuguese (Brazil)")]
    [InlineData("es-419", "Spanish (Latino)")]
    [InlineData("und", "Unknown")]
    [InlineData("xyz", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Track_languages_map_to_streamzh_languages(string? code, string expected) =>
        Assert.Equal(expected, MediaLanguages.FromCode(code).Name);

    [Fact]
    public void Broken_output_is_reported()
    {
        Assert.Throws<MediaInfoException>(() => FfprobeParser.Parse("not json"));
        Assert.Throws<MediaInfoException>(() => FfprobeParser.Parse("{}"));
    }

    [Fact]
    public void Media_info_survives_the_json_round_trip()
    {
        var info = DolbyVision();
        var json = System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var back = StreamZH.Data.MediaFileDetailsRepository.Deserialize(json)!;

        Assert.Contains("\"hdr\":\"DolbyVisionHdr10\"", json, StringComparison.Ordinal);
        Assert.Equal(info.Video, back.Video);
        Assert.Equal(info.AudioTracks, back.AudioTracks);
        Assert.Equal(info.Subtitles, back.Subtitles);
        Assert.Null(StreamZH.Data.MediaFileDetailsRepository.Deserialize("{broken"));
    }

    private const string Hdr10PlusFrame = """
        {"frames":[{"side_data_list":[
          {"side_data_type":"Mastering display metadata","max_luminance":"10000000/10000"},
          {"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)","application version":1,"num_windows":1}]}]}
        """;

    private static string WithoutStreamSideData(string json)
    {
        var node = JsonNode.Parse(json)!;
        node["streams"]![0]!.AsObject().Remove("side_data_list");
        return node.ToJsonString();
    }
}
