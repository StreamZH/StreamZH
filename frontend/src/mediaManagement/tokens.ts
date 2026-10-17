/** Naming tokens per format, grouped like Radarr's and Sonarr's token list. `example` shows what the sample gives. */
export interface TokenGroup {
  name: string
  tokens: { token: string; example: string }[]
}

const movie: TokenGroup = {
  name: 'Movie',
  tokens: [
    { token: '{Movie Title}', example: "The Movie Title's! Part Two" },
    { token: '{Movie CleanTitle}', example: 'The Movie Titles Part Two' },
    { token: '{Movie TitleThe}', example: "Movie Title's!, The" },
    { token: '{Movie OriginalTitle}', example: 'Le Titre du Film' },
    { token: '{Movie TitleFirstCharacter}', example: 'M' },
    { token: '{Release Year}', example: '2010' },
  ],
}

const movieIds: TokenGroup = {
  name: 'Ids',
  tokens: [
    { token: '{ImdbId}', example: 'tt0066921' },
    { token: '{TmdbId}', example: '345691' },
    { token: '{imdb-{ImdbId}}', example: '{imdb-tt0066921}' },
    { token: '{tmdb-{TmdbId}}', example: '{tmdb-345691}' },
  ],
}

const seriesIds: TokenGroup = {
  name: 'Ids',
  tokens: [
    { token: '{ImdbId}', example: 'tt12345' },
    { token: '{TvdbId}', example: '12345' },
    { token: '{TmdbId}', example: '9999' },
    { token: '{tvdb-{TvdbId}}', example: '{tvdb-12345}' },
  ],
}

const quality: TokenGroup = {
  name: 'Quality',
  tokens: [
    { token: '{Quality Full}', example: 'Bluray-1080p Proper' },
    { token: '{Quality Title}', example: 'Bluray-1080p' },
    { token: '{Quality Proper}', example: 'Proper' },
    { token: '{Quality Real}', example: 'REAL' },
  ],
}

const mediaInfo: TokenGroup = {
  name: 'Media info (from the release name)',
  tokens: [
    { token: '{MediaInfo VideoCodec}', example: 'x264' },
    { token: '{MediaInfo AudioCodec}', example: 'DTS' },
    { token: '{MediaInfo Simple}', example: 'x264 DTS' },
    { token: '{MediaInfo VideoDynamicRange}', example: 'HDR' },
    { token: '{MediaInfo AudioLanguages}', example: 'DE+EN' },
  ],
}

const release = (edition: boolean): TokenGroup => ({
  name: 'Release',
  tokens: [
    ...(edition
      ? [
          { token: '{Edition Tags}', example: 'IMAX' },
          { token: '{edition-{Edition Tags}}', example: '{edition-IMAX}' },
        ]
      : []),
    { token: '{Release Group}', example: 'RlsGrp' },
    { token: '{-Release Group}', example: '-RlsGrp' },
    { token: '{Original Title}', example: 'The.Movie.Title.2010.1080p.BluRay-RlsGrp' },
    { token: '{Original Filename}', example: 'the.movie.title.2010.1080p-rlsgrp' },
  ],
})

const series: TokenGroup = {
  name: 'Series',
  tokens: [
    { token: '{Series Title}', example: "The Series Title's!" },
    { token: '{Series TitleYear}', example: "The Series Title's! (2010)" },
    { token: '{Series CleanTitle}', example: 'The Series Titles' },
    { token: '{Series TitleThe}', example: "Series Title's!, The" },
    { token: '{Series TitleFirstCharacter}', example: 'S' },
    { token: '{Series Year}', example: '2010' },
  ],
}

const season: TokenGroup = {
  name: 'Season',
  tokens: [
    { token: '{season}', example: '1' },
    { token: '{season:00}', example: '01' },
  ],
}

const episode: TokenGroup = {
  name: 'Episode',
  tokens: [
    { token: '{season:00}', example: '01' },
    { token: '{episode:00}', example: '01' },
    { token: 'S{season:00}E{episode:00}', example: 'S01E01' },
    { token: '{season}x{episode:00}', example: '1x01' },
    { token: '{Episode Title}', example: 'Episode Title (1)' },
    { token: '{Episode CleanTitle}', example: 'Episode Title 1' },
    { token: '{Air-Date}', example: '2013-10-30' },
    { token: '{Air Date}', example: '2013 10 30' },
  ],
}

export const tokenGroups = {
  movieFile: [movie, quality, release(true), mediaInfo, movieIds],
  movieFolder: [movie, movieIds],
  episodeFile: [series, episode, quality, release(false), mediaInfo, seriesIds],
  seriesFolder: [series, seriesIds],
  seasonFolder: [season, series],
}

export type TokenKind = keyof typeof tokenGroups
