# Collection of Custom Formats for Sonarr

{! include-markdown "../../includes/starr/cf-not-compatible.md" !}

Below is a collection of what we've come to regard as the most needed and commonly used Custom Formats.
These CFs have been collected from discussions on Discord or created with help from others.

Special thanks to everyone who has helped in the creation and testing of these Custom Formats, my team at TRaSH guides, and the community.

- [How to import Custom Formats](/Sonarr/sonarr-import-custom-formats){:target="\_blank" rel="noopener noreferrer"} - Explains how to import the Custom Formats.
- [How to upgrade Custom Formats](/Sonarr/sonarr-how-to-update-custom-formats){:target="\_blank" rel="noopener noreferrer"} - Explains how to upgrade your existing Custom Formats.
- [How to set up Quality Profiles](/Sonarr/sonarr-setup-quality-profiles){:target="\_blank" rel="noopener noreferrer"} - Explains how to make the most use of Custom Formats and shows some specifics of my configuration. You can use these examples to get an idea of how to set up your preferences.

!!! tip

    It also is recommended to change the Propers and Repacks settings in Sonarr's `Settings`.

    `Media Management` => `File Management` to `Do Not Prefer` and use the [Repack/Proper](#repackproper) Custom Format.

    ![!cf-mm-propers-repacks-disable](images/cf-mm-propers-repacks-disable.png)

    This way you make sure the Custom Format scoring and preferences will be fully utilized.

--8<-- "includes/support.md"

---

## INDEX

---

| Audio Formats                         | Audio Channels               | HDR Formats                      | HDR Formats (Optional)                       |
|---------------------------------------|------------------------------|----------------------------------|----------------------------------------------|
| [TrueHD ATMOS](#truehd-atmos)         | [1.0 Mono](#10-mono)         | [HDR](#hdr)                      | [DV (Disk)](#dv-disk)                        |
| [DTS X](#dts-x)                       | [2.0 Stereo](#20-stereo)     | [DV Boost](#dv-boost)            | [DV (w/o HDR fallback)](#dv-wo-hdr-fallback) |
| [ATMOS (undefined)](#atmos-undefined) | [3.0 Sound](#30-sound)       | [HDR10+ Boost](#hdr10plus-boost) | [SDR](#sdr)                                  |
| [DD+ ATMOS](#ddplus-atmos)            | [4.0 Sound](#40-sound)       |                                  | [SDR (no WEBDL)](#sdr-no-webdl)              |
| [TrueHD](#truehd)                     | [5.1 Surround](#51-surround) |                                  |                                              |
| [DTS-HD MA](#dts-hd-ma)               | [6.1 Surround](#61-surround) |                                  |                                              |
| [DD+](#ddplus)                        | [7.1 Surround](#71-surround) |                                  |                                              |
| [DTS-ES](#dts-es)                     |                              |                                  |                                              |
| [DTS](#dts)                           |                              |                                  |                                              |
| [FLAC](#flac)                         |                              |                                  |                                              |
| [PCM](#pcm)                           |                              |                                  |                                              |
| [DTS-HD HRA](#dts-hd-hra)             |                              |                                  |                                              |
| [AAC](#aac)                           |                              |                                  |                                              |
| [DD](#dd)                             |                              |                                  |                                              |
| [MP3](#mp3)                           |                              |                                  |                                              |
| [Opus](#opus)                         |                              |                                  |                                              |

---

| Streaming Services General | Streaming Services Asian  | Streaming Services Dutch |
|----------------------------|---------------------------|--------------------------|
| [Amazon](#amzn)            | [Coupang Play](#cpng)     | [NLZiet](#nlz)           |
| [Apple TV](#atv)           | [Disney+ Hotstar](#htsr)  | [Videoland](#vdl)        |
| [Apple TV+](#atvp)         | [DMM-TV](#dmm-tv)         |                          |
| [Comedy Central](#cc)      | [friDay Video](#friday)   |                          |
| [DC Universe](#dcu)        | [Fuji-TV On Demand](#fod) |                          |
| [Disney+](#dsnp)           | [Hami Video](#hami)       |                          |
| [Google Play](#play)       | [iQIYI](#iqiy)            |                          |
| [HBO](#hbo)                | [KKTV](#kktv)             |                          |
| [HBO Max](#hmax)           | [KOCOWA](#kcw)            |                          |
| [Hulu](#hulu)              | [LINE TV](#linetv)        |                          |
| [iTunes](#it)              | [myTV SUPER](#mytvsuper)  |                          |
| [Max](#max)                | [TVING](#tving)           |                          |
| [Netflix](#nf)             | [TVer](#tver)             |                          |
| [Paramount+](#pmtp)        | [U-NEXT](#u-next)         |                          |
| [Peacock TV](#pcok)        | [Viki](#viki)             |                          |
| [ROKU](#roku)              | [VIU](#viu)               |                          |
| [SHOWTIME](#sho)           | [Wavve](#wavve)           |                          |
| [Stan](#stan)              | [WeTV](#wetv)             |                          |
| [Syfy](#syfy)              | [Youku](#youku)           |                          |

| Streaming Services Misc | Streaming Services UK | Streaming Services Optional                 |
|-------------------------|-----------------------|---------------------------------------------|
| [AUBC](#aubc)           | [4OD](#4od)           | [HD Streaming Boost](#hd-streaming-boost)   |
| [Canal+](#cnlp)         | [ALL4](#all4)         | [UHD Streaming Boost](#uhd-streaming-boost) |
| [CBC](#cbc)             | [BBC iPlayer](#ip)    |                                             |
| [Crave](#crav)          | [ITVX](#itvx)         |                                             |
| [Discovery+](#dscp)     | [MY5](#my5)           |                                             |
| [Fandango](#fah)        | [NOW](#now)           |                                             |
| [OViD](#ovid)           |                       |                                             |
| [Quibi](#qibi)          |                       |                                             |
| [Star+](#strp)          |                       |                                             |
| [YouTube Red](#red)     |                       |                                             |

| Streaming Services French | Streaming Services Anime |
|---------------------------|--------------------------|
| [AUViO](#auvio)           | [ABEMA](#abema)          |
| [MyCANAL](#mycanal)       | [ADN](#adn)              |
| [SALTO](#salto)           | [B-Global](#b-global)    |
|                           | [Bilibili](#bilibili)    |
|                           | [Crunchyroll](#cr)       |
|                           | [Funimation](#funi)      |
|                           | [HIDIVE](#hidive)        |
|                           | [VRV](#vrv)              |
|                           | [WKN](#wkn)              |

---

| Series Versions       | Unwanted                                | HQ Release Groups                       |
|-----------------------|-----------------------------------------|-----------------------------------------|
| [Hybrid](#hybrid)     | [AV1](#av1)                             | [Remux Tier 01](#remux-tier-01)         |
| [Remaster](#remaster) | [Bad Dual Groups](#bad-dual-groups)     | [Remux Tier 02](#remux-tier-02)         |
|                       | [BR-DISK](#br-disk)                     | [HD Bluray Tier 01](#hd-bluray-tier-01) |
|                       | [BR-DISK (BTN)](#br-disk-btn)           | [HD Bluray Tier 02](#hd-bluray-tier-02) |
|                       | [Black & White](#bw)                    | [WEB Tier 01](#web-tier-01)             |
|                       | [Extras](#extras)                       | [WEB Tier 02](#web-tier-02)             |
|                       | [LQ](#lq)                               | [WEB Tier 03](#web-tier-03)             |
|                       | [LQ (Release Title)](#lq-release-title) | [WEB Scene](#web-scene)                 |
|                       | [No-RlsGroup](#no-rlsgroup)             |                                         |
|                       | [Obfuscated](#obfuscated)               |                                         |
|                       | [Retags](#retags)                       |                                         |
|                       | [Scene](#scene)                         |                                         |
|                       | [Upscaled](#upscaled)                   |                                         |

---

| Repacks/Proper (Required)      | Golden Rule (Optional)             | Miscellaneous (Optional)      |
|--------------------------------|------------------------------------|-------------------------------|
| [Repack/Proper](#repackproper) | [x265 (no HDR/DV)](#x265-no-hdrdv) | [FreeLeech](#freeleech)       |
| [Repack2](#repack2)            | [x265 (HD)](#x265-hd)              | [HFR](#hfr)                   |
| [Repack3](#repack3)            |                                    | [Internal](#internal)         |
|                                |                                    | [MPEG2](#mpeg2)               |
|                                |                                    | [Multi](#multi)               |
|                                |                                    | [P2P Internal](#p2p-internal) |
|                                |                                    | [VC-1](#vc-1)                 |
|                                |                                    | [VP9](#vp9)                   |
|                                |                                    | [x264](#x264)                 |
|                                |                                    | [x265](#x265)                 |
|                                |                                    | [x266](#x266)                 |

---

| Resolution (Optional) | Release Type (Optional)           |
|-----------------------|-----------------------------------|
| [720p](#720p)         | [Multi-Episode](#multi-episode)   |
| [1080p](#1080p)       | [Season Packs](#season-pack)      |
| [2160p](#2160p)       | [Single Episode](#single-episode) |

---

| Language profiles (Optional)                                                                       | Accessibility (Optional) |
|----------------------------------------------------------------------------------------------------|--------------------------|
| [German](#german)                                                                                  | [WiTH AD](#with-ad)      |
| [German DL](#german-dl)                                                                            | [WiTH ASL](#with-asl)    |
| [German DL (undefined)](#german-dl-undefined)                                                      | [WiTH BASL](#with-basl)  |
| [Language: Not English (English Only)](#language-not-english)                                      | [WiTH BSL](#with-bsl)    |
| [Language: Not French (French Only)](#language-not-french)                                         |                          |
| [Language: Not Original (Original Only)](#language-not-original)                                   |                          |
| [Language: Original + French](#language-original-plus-french)                                      |                          |
| [Not German or English](#not-german-or-english)                                                    |                          |
| [Not German, Japanese or English](#not-german-japanese-or-english)                                 |                          |
| [Not German, Japanese, Korean, Chinese or English](#not-german-japanese-korean-chinese-or-english) |                          |
| [Wrong Language](#wrong-language)                                                                  |                          |

---

| Anime Release Groups                  | Anime Release Groups                    | Anime Versions | Anime Optional                        |
|---------------------------------------|-----------------------------------------|----------------|---------------------------------------|
| [Anime BD Tier 01](#anime-bd-tier-01) | [Anime Web Tier 01](#anime-web-tier-01) | [v0](#v0)      | [10bit](#10bit)                       |
| [Anime BD Tier 02](#anime-bd-tier-02) | [Anime Web Tier 02](#anime-web-tier-02) | [v1](#v1)      | [Anime Dual Audio](#anime-dual-audio) |
| [Anime BD Tier 03](#anime-bd-tier-03) | [Anime Web Tier 03](#anime-web-tier-03) | [v2](#v2)      | [Dubs Only](#dubs-only)               |
| [Anime BD Tier 04](#anime-bd-tier-04) | [Anime Web Tier 04](#anime-web-tier-04) | [v3](#v3)      | [Uncensored](#uncensored)             |
| [Anime BD Tier 05](#anime-bd-tier-05) | [Anime Web Tier 05](#anime-web-tier-05) | [v4](#v4)      |                                       |
| [Anime BD Tier 06](#anime-bd-tier-06) | [Anime Web Tier 06](#anime-web-tier-06) |                |                                       |
| [Anime BD Tier 07](#anime-bd-tier-07) | [Anime Raws](#anime-raws)               |                |                                       |
| [Anime BD Tier 08](#anime-bd-tier-08) | [Anime LQ Groups](#anime-lq-groups)     |                |                                       |

---

| German Release Groups                           | German Anime Release Groups                                 | German Miscellaneous                                  |
|-------------------------------------------------|-------------------------------------------------------------|-------------------------------------------------------|
| [German Remux Tier 01](#german-remux-tier-01)   | [German Anime Bluray Tier 01](#german-anime-bluray-tier-01) | [German 1080p Booster](#german-1080p-booster)         |
| [German Remux Tier 02](#german-remux-tier-02)   | [German Anime Bluray Tier 02](#german-anime-bluray-tier-02) | [German 2160p Booster](#german-2160p-booster)         |
| [German Bluray Tier 01](#german-bluray-tier-01) | [German Anime Bluray Tier 03](#german-anime-bluray-tier-03) | [German LQ](#german-lq)                               |
| [German Bluray Tier 02](#german-bluray-tier-02) | [German Anime Web Tier 01](#german-anime-web-tier-01)       | [German LQ (Release Title)](#german-lq-release-title) |
| [German Bluray Tier 03](#german-bluray-tier-03) | [German Anime Web Tier 02](#german-anime-web-tier-02)       | [German Microsized](#german-microsized)               |
| [German Web Tier 01](#german-web-tier-01)       | [German Anime Web Tier 03](#german-anime-web-tier-03)       | [German Subbed](#german-subbed)                       |
| [German Web Tier 02](#german-web-tier-02)       | [German Anime Scene](#german-anime-scene)                   |                                                       |
| [German Web Tier 03](#german-web-tier-03)       |                                                             |                                                       |
| [German Scene](#german-scene)                   |                                                             |                                                       |

---

| French Release Groups                         | French Anime Release Groups           | French Audio Version |
|-----------------------------------------------|---------------------------------------|----------------------|
| [FR Remux Tier 01](#fr-remux-tier-01)         | [FR Anime Tier 01](#fr-anime-tier-01) | [VFF](#vff)          |
| [FR HD Bluray Tier 01](#fr-hd-bluray-tier-01) | [FR Anime Tier 02](#fr-anime-tier-02) | [VOF](#vof)          |
| [FR WEB Tier 01](#fr-web-tier-01)             | [FR Anime Tier 03](#fr-anime-tier-03) | [VFI](#vfi)          |
| [FR WEB Tier 02](#fr-web-tier-02)             | [FR Anime FanSub](#fr-anime-fansub)   | [VF2](#vf2)          |
| [FR WEB Tier 03](#fr-web-tier-03)             |                                       | [VFQ](#vfq)          |
| [FR Scene Groups](#fr-scene-groups)           |                                       | [VOQ](#voq)          |
| [FR LQ](#fr-lq)                               |                                       | [VQ](#vq)            |
|                                               |                                       | [VFB](#vfb)          |
|                                               |                                       | [VOSTFR](#vostfr)    |
|                                               |                                       | [FanSUB](#fansub)    |
|                                               |                                       | [FastSUB](#fastsub)  |

---

<!-- START OF THE COLLECTION LIST -->

---

