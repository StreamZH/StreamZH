# Collection of Custom Formats for Radarr

{! include-markdown "../../includes/starr/cf-not-compatible.md" !}

Below is a collection of what we've come to regard as the most needed and commonly used Custom Formats.
These CFs have been collected from discussions on Discord or created with help from others.

Special thanks to everyone who has helped in the creation and testing of these Custom Formats, my team at TRaSH guides, Team Radarr, and the community.

After requesting with Team Radarr, we now have the option to import/export the Custom Formats in a JSON format. This provides everyone with an easy way to share and use Custom Formats.

We've made 3 guides related to this.

- [How to import Custom Formats](/Radarr/Radarr-import-custom-formats){:target="\_blank" rel="noopener noreferrer"} - Explains how to import the Custom Formats.
- [How to upgrade Custom Formats](/Radarr/Radarr-how-to-update-custom-formats){:target="\_blank" rel="noopener noreferrer"} - Explains how to upgrade your existing Custom Formats.
- [How to set up Quality Profiles](/Radarr/radarr-setup-quality-profiles){:target="\_blank" rel="noopener noreferrer"} - Explains how to make the most use of Custom Formats and shows some specifics of our recommended configuration. You can use these examples to get an idea of how to set up your preferences.

!!! tip

    It also is recommended to change the Propers and Repacks settings in Radarr's `Settings`.

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
| [Amazon](#amzn)            | [Coupang Play](#cpng)     | [Pathe Thuis](#pathe)    |
| [Apple TV](#atv)           | [Disney+ Hotstar](#htsr)  | [Videoland](#vdl)        |
| [Apple TV+](#atvp)         | [friDay Video](#friday)   |                          |
| [Bravia Core](#bcore)      | [Fuji-TV On Demand](#fod) |                          |
| [Criterion Channel](#crit) | [Hami Video](#hami)       |                          |
| [Disney+](#dsnp)           | [iQIYI](#iqiy)            |                          |
| [Google Play](#play)       | [KKTV](#kktv)             |                          |
| [HBO](#hbo)                | [KOCOWA](#kcw)            |                          |
| [HBO Max](#hmax)           | [LINE TV](#linetv)        |                          |
| [Hulu](#hulu)              | [myTV SUPER](#mytvsuper)  |                          |
| [IMF](#imf)                | [TVING](#tving)           |                          |
| [iTunes](#it)              | [TVer](#tver)             |                          |
| [Max](#max)                | [U-NEXT](#u-next)         |                          |
| [Movies Anywhere](#ma)     | [Viki](#viki)             |                          |
| [Netflix](#nf)             | [VIU](#viu)               |                          |
| [Paramount+](#pmtp)        | [Wavve](#wavve)           |                          |
| [Peacock TV](#pcok)        | [WeTV](#wetv)             |                          |
| [ROKU](#roku)              | [Youku](#youku)           |                          |
| [Stan](#stan)              |                           |                          |
|                            |                           |                          |

| Streaming Services Misc | Streaming Services UK | Streaming Services Anime |
|-------------------------|-----------------------|--------------------------|
| [AUBC](#aubc)           | [BBC iPlayer](#ip)    | [Funimation](#funi)      |
| [Canal+](#cnlp)         | [ITVX](#itvx)         | [VRV](#vrv)              |
| [CBC](#cbc)             | [MY5](#my5)           |                          |
| [Crave](#crav)          | [NOW](#now)           |                          |
| [Fandango](#fah)        |                       |                          |
| [OViD](#ovid)           |                       |                          |
| [Star+](#strp)          |                       |                          |

---

| Movie Versions (Optional)                     | Unwanted Formats                                      | HQ Release Groups                         |
|-----------------------------------------------|-------------------------------------------------------|-------------------------------------------|
| [4K Remaster](#4k-remaster)                   | [3D](#3d)                                             | [Remux Tier 01](#remux-tier-01)           |
| [Criterion Collection](#criterion-collection) | [AV1](#av1)                                           | [Remux Tier 02](#remux-tier-02)           |
| [Hybrid](#hybrid)                             | [Bad Dual Groups](#bad-dual-groups)                   | [Remux Tier 03](#remux-tier-03)           |
| [IMAX](#imax)                                 | [Black and White Editions](#black-and-white-editions) | [UHD Bluray Tier 01](#uhd-bluray-tier-01) |
| [IMAX Enhanced](#imax-enhanced)               | [BR-DISK](#br-disk)                                   | [UHD Bluray Tier 02](#uhd-bluray-tier-02) |
| [Masters of Cinema](#masters-of-cinema)       | [Extras](#extras)                                     | [UHD Bluray Tier 03](#uhd-bluray-tier-03) |
| [Open Matte](#open-matte)                     | [Generated Dynamic HDR](#generated-dynamic-hdr)       | [HD Bluray Tier 01](#hd-bluray-tier-01)   |
| [Remaster](#remaster)                         | [Line/Mic Dubbed](#linemic-dubbed)                    | [HD Bluray Tier 02](#hd-bluray-tier-02)   |
| [Special Edition](#special-edition)           | [LQ](#lq)                                             | [HD Bluray Tier 03](#hd-bluray-tier-03)   |
| [Theatrical Cut](#theatrical-cut)             | [LQ (Release Title)](#lq-release-title)               | [WEB Tier 01](#web-tier-01)               |
| [Vinegar Syndrome](#vinegar-syndrome)         | [No-RlsGroup](#no-rlsgroup)                           | [WEB Tier 02](#web-tier-02)               |
|                                               | [Obfuscated](#obfuscated)                             | [WEB Tier 03](#web-tier-03)               |
|                                               | [Retags](#retags)                                     |                                           |
|                                               | [Scene](#scene)                                       |                                           |
|                                               | [Sing-Along Versions](#sing-along-versions)           |                                           |
|                                               | [Upscaled](#upscaled)                                 |                                           |

---

| Repacks/Proper (Required)      | Golden Rule (Optional)             | Miscellaneous (Optional)      | Resolution (Optional) |
|--------------------------------|------------------------------------|-------------------------------|-----------------------|
| [Repack/Proper](#repackproper) | [x265 (no HDR/DV)](#x265-no-hdrdv) | [FreeLeech](#freeleech)       | [720p](#720p)         |
| [Repack2](#repack2)            | [x265 (HD)](#x265-hd)              | [HFR](#hfr)                   | [1080p](#1080p)       |
| [Repack3](#repack3)            |                                    | [Internal](#internal)         | [2160p](#2160p)       |
|                                |                                    | [MPEG2](#mpeg2)               |                       |
|                                |                                    | [Multi](#multi)               |                       |
|                                |                                    | [P2P Internal](#p2p-internal) |                       |
|                                |                                    | [VC-1](#vc-1)                 |                       |
|                                |                                    | [VP9](#vp9)                   |                       |
|                                |                                    | [x264](#x264)                 |                       |
|                                |                                    | [x265](#x265)                 |                       |
|                                |                                    | [x266](#x266)                 |                       |

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

| German Release Groups                           | German Anime Release Groups                                 | German Miscellaneous                          | German Unwanted                                       |
|-------------------------------------------------|-------------------------------------------------------------|-----------------------------------------------|-------------------------------------------------------|
| [German Remux Tier 01](#german-remux-tier-01)   | [German Anime Bluray Tier 01](#german-anime-bluray-tier-01) | [German 1080p Booster](#german-1080p-booster) | [German LQ](#german-lq)                               |
| [German Remux Tier 02](#german-remux-tier-02)   | [German Anime Bluray Tier 02](#german-anime-bluray-tier-02) | [German 2160p Booster](#german-2160p-booster) | [German LQ (Release Title)](#german-lq-release-title) |
| [German Bluray Tier 01](#german-bluray-tier-01) | [German Anime Bluray Tier 03](#german-anime-bluray-tier-03) | [German Subbed](#german-subbed)               | [German Microsized](#german-microsized)               |
| [German Bluray Tier 02](#german-bluray-tier-02) | [German Anime Web Tier 01](#german-anime-web-tier-01)       |                                               |                                                       |
| [German Bluray Tier 03](#german-bluray-tier-03) | [German Anime Web Tier 02](#german-anime-web-tier-02)       |                                               |                                                       |
| [German Web Tier 01](#german-web-tier-01)       | [German Anime Web Tier 03](#german-anime-web-tier-03)       |                                               |                                                       |
| [German Web Tier 02](#german-web-tier-02)       | [German Anime Scene](#german-anime-scene)                   |                                               |                                                       |
| [German Web Tier 03](#german-web-tier-03)       |                                                             |                                               |                                                       |
| [German Scene](#german-scene)                   |                                                             |                                               |                                                       |

---

| French Release Groups                           | French Anime Release Groups             | French Audio Version |
|-------------------------------------------------|-----------------------------------------|----------------------|
| [FR Remux Tier 01](#fr-remux-tier-01)           | [FR Anime Tier 01](#fr-anime-tier-01)   | [VFF](#vff)          |
| [FR Remux Tier 02](#fr-remux-tier-02)           | [FR Anime Tier 02](#fr-anime-tier-02)   | [VOF](#vof)          |
| [FR UHD Bluray Tier 01](#fr-uhd-bluray-tier-01) | [FR Anime Tier 03](#fr-anime-tier-03)   | [VFI](#vfi)          |
| [FR UHD Bluray Tier 02](#fr-uhd-bluray-tier-02) | [FR Anime FanSub](#fr-anime-fansub)     | [VF2](#vf2)          |
| [FR HD Bluray Tier 01](#fr-hd-bluray-tier-01)   |                                         | [VFQ](#vfq)          |
| [FR HD Bluray Tier 02](#fr-hd-bluray-tier-02)   |                                         | [VOQ](#voq)          |
| [FR WEB Tier 01](#fr-web-tier-01)               |                                         | [VQ](#vq)            |
| [FR WEB Tier 02](#fr-web-tier-02)               |                                         | [VFB](#vfb)          |
| [FR Scene Groups](#fr-scene-groups)             |                                         | [VOSTFR](#vostfr)    |
| [FR LQ](#fr-lq)                                 |                                         | [FanSUB](#fansub)    |
|                                                 |                                         | [FastSUB](#fastsub)  |

---

