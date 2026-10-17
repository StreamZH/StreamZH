// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// Portions ported from Radarr (https://github.com/Radarr/Radarr) and Sonarr (https://github.com/Sonarr/Sonarr), Copyright (c) their contributors, GPL-3.0.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Parsing.Releases;

namespace StreamZH.Tests.Parsing;

/// <summary>Clean titles, ported from Radarr's NormalizeTitleFixture and Sonarr's NormalizeSeriesTitleFixture.</summary>
public class TitleNormalizerTests
{
    [Theory]
    [InlineData("Conan", "conan")]
    [InlineData("Castle (2009)", "castle2009")]
    [InlineData("Parenthood.2010", "parenthood2010")]
    [InlineData("Law_and_Order_SVU", "lawordersvu")]
    [InlineData("CaPitAl", "capital")]
    [InlineData("peri.od", "period")]
    [InlineData("this.^&%^**$%@#$!That", "thisthat")]
    [InlineData("test/test", "testtest")]
    [InlineData("90210", "90210")]
    [InlineData("24", "24")]
    [InlineData("I'm a cyborg, but that's OK", "imcyborgbutthatsok")]
    [InlineData("Im a cyborg, but thats ok", "imcyborgbutthatsok")]
    [InlineData("Test: Something à Deux", "testsomethingdeux")]
    [InlineData("Parler à", "parlera")]
    [InlineData("The Office", "theoffice")]
    [InlineData("The Tonight Show With Jay Leno", "thetonightshowwithjayleno")]
    [InlineData("The.Daily.Show", "thedailyshow")]
    [InlineData("Series", "series")]
    [InlineData("Series (2009)", "series2009")]
    [InlineData("Series.2010", "series2010")]
    [InlineData("Series_and_Title_Sonarr", "seriestitlesonarr")]
    [InlineData("Ríkið", "rikid")]
    [InlineData("The Series", "theseries")]
    [InlineData("The Series Show With Sonarr Dev", "theseriesshowwithsonarrdev")]
    [InlineData("The.Series.Show", "theseriesshow")]
    [InlineData("3%", "3percent")]
    [InlineData("Series Top & 100% Coding Developers", "seriestop100percentcodingdevelopers")]
    [InlineData("Series Title What's Your F@%king Deal?!", "seriestitlewhatsyourfkingdeal")]
    [InlineData("Der Teufel trägt Prada 2", "derteufeltraegtprada2")]
    [InlineData("Der.Teufel.traegt.Prada.2", "derteufeltraegtprada2")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Carnivàle", "carnivale")]
    [InlineData("Seriàes", "seriaes")]
    [InlineData("Straße", "strasse")]
    [InlineData("Rock & Roll", "rockroll")]
    [InlineData("The Pope's Exorcist", "thepopesexorcist")]
    [InlineData("Blade Runner 2049", "bladerunner2049")]
    [InlineData("1917", "1917")]
    [InlineData("", "")]
    public void Cleans_titles(string title, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Clean(title));
    }

    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("an")]
    [InlineData("of")]
    public void Removes_common_words_from_the_middle(string word)
    {
        foreach (var format in new[] { "word.{0}.word", "word {0} word", "word-{0}-word" })
        {
            Assert.Equal("wordword", TitleNormalizer.Clean(string.Format(format, word)));
        }
    }

    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("an")]
    [InlineData("of")]
    public void Keeps_common_words_at_the_end(string word)
    {
        foreach (var format in new[] { "word.word.{0}", "word-word-{0}", "word-word {0}" })
        {
            Assert.Equal("wordword" + word, TitleNormalizer.Clean(string.Format(format, word)));
        }
    }

    [Fact]
    public void Removes_a_from_the_middle()
    {
        foreach (var format in new[] { "word.{0}.word", "word {0} word", "word-{0}-word" })
        {
            Assert.Equal("wordword", TitleNormalizer.Clean(string.Format(format, "a")));
        }
    }

    [Fact]
    public void Keeps_a_at_the_start_of_an_acronym()
    {
        foreach (var format in new[] { "word.{0}.N.K.L.E.word", "word {0} N K L E word", "word-{0}-N-K-L-E-word" })
        {
            Assert.Equal("wordankleword", TitleNormalizer.Clean(string.Format(format, "a")));
        }
    }

    [Fact]
    public void Keeps_a_at_the_end_of_an_acronym()
    {
        foreach (var format in new[] { "word.N.K.L.E.{0}.word", "word N K L E {0} word", "word-N-K-L-E-{0}-word" })
        {
            Assert.Equal("wordnkleaword", TitleNormalizer.Clean(string.Format(format, "a")));
        }
    }

    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("a")]
    [InlineData("an")]
    [InlineData("of")]
    public void Keeps_common_words_inside_other_words(string word)
    {
        foreach (var format in new[] { "word.{0}word", "word {0}word", "word-{0}word", "word{0}.word", "word{0}-word" })
        {
            Assert.Equal("word" + word + "word", TitleNormalizer.Clean(string.Format(format, word)));
        }
    }

    [Theory]
    [InlineData("the")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("a")]
    [InlineData("an")]
    [InlineData("of")]
    public void Keeps_common_words_at_the_start(string word)
    {
        foreach (var format in new[] { "{0}.word.word", "{0}-word-word", "{0} word word" })
        {
            Assert.Equal(word + "wordword", TitleNormalizer.Clean(string.Format(format, word)));
        }
    }

    [Theory]
    [InlineData("Tokyo Ghoul A", "tokyoghoula")]
    [InlineData("Series Title A", "seriestitlea")]
    public void Keeps_a_trailing_a(string title, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Clean(title));
    }

    [Theory]
    [InlineData("Der.Teufel.traegt.Prada.2.2026.German.5.1.Line.Dubbed.1080p.DCPRip.x264-CMN", "Der Teufel trägt Prada 2")]
    [InlineData("Amelie.2001.1080p.BluRay.x264-GRP", "Amélie")]
    [InlineData("Law.and.Order.S01E01.720p", "Law & Order")]
    public void Release_clean_title_matches_library_title(string release, string libraryTitle)
    {
        Assert.Equal(TitleNormalizer.Clean(libraryTitle), ReleaseParser.Parse(release)?.CleanTitle);
    }
}
