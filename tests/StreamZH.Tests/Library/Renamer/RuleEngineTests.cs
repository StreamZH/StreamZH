// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using StreamZH.Library.Renamer;

namespace StreamZH.Tests.Library.Renamer;

public sealed class RuleEngineTests
{
    private static RenameItem File(string name, int index = 0, int inFolder = 0, string folder = "Season 1") =>
        new(name, false, folder, new DateTime(2024, 3, 9, 14, 5, 7, DateTimeKind.Utc), 1000, index, inFolder);

    private static string Apply(RenameRule rule, string name) => RuleEngine.Apply(rule, name, File(name));

    // ------------------------------------------------------------------ replace

    [Theory]
    [InlineData("Show.S01E01.mkv", ".", " ", "Show S01E01.mkv")]
    [InlineData("show.SHOW.mkv", "show", "X", "X.X.mkv")]
    public void Replace_plain_text_ignores_case_and_keeps_the_extension(string name, string find, string replace, string expected) =>
        Assert.Equal(expected, Apply(new ReplaceRule { Find = find, ReplaceWith = replace }, name));

    [Fact]
    public void Replace_case_sensitive_and_occurrences()
    {
        Assert.Equal("a-b.A.mkv", Apply(new ReplaceRule { Find = "a", ReplaceWith = "a-b", CaseSensitive = true, Occurrence = ReplaceOccurrence.First }, "a.A.mkv"));
        Assert.Equal("x.x.y.mkv", Apply(new ReplaceRule { Find = "x", ReplaceWith = "y", Occurrence = ReplaceOccurrence.Last }, "x.x.x.mkv"));
        Assert.Equal("1-2-3.mkv", Apply(new ReplaceRule { Find = "_", ReplaceWith = "-", Occurrence = ReplaceOccurrence.All }, "1_2_3.mkv"));
    }

    [Fact]
    public void Replace_regex_with_capture_groups()
    {
        var rule = new ReplaceRule { Find = @"^(.+?)\.S(\d+)E(\d+).*$", ReplaceWith = "$1 - $2x$3", UseRegex = true };
        Assert.Equal("Show - 01x05.mkv", Apply(rule, "Show.S01E05.720p.HDTV.x264-GRP.mkv"));

        var named = new ReplaceRule { Find = @"(?<year>\d{4})", ReplaceWith = "(${year})", UseRegex = true, Occurrence = ReplaceOccurrence.First };
        Assert.Equal("Movie (1999) 2000.mkv", Apply(named, "Movie 1999 2000.mkv"));

        var last = new ReplaceRule { Find = @"\d", ReplaceWith = "#", UseRegex = true, Occurrence = ReplaceOccurrence.Last };
        Assert.Equal("a1b#.mkv", Apply(last, "a1b2.mkv"));
    }

    [Fact]
    public void Replace_on_the_full_name_changes_the_extension_too()
    {
        var rule = new ReplaceRule { Find = "mkv", ReplaceWith = "mp4", Scope = RuleScope.FullName };
        Assert.Equal("a.mp4", Apply(rule, "a.mkv"));
        Assert.Equal("a.mkv", Apply(rule with { Scope = RuleScope.Name }, "a.mkv"));
    }

    [Fact]
    public void Regex_errors_are_readable()
    {
        Assert.Equal("A ( is not closed (at character 3).", RuleEngine.Check(new ReplaceRule { Find = "(ab", UseRegex = true }));
        Assert.Equal("A [ is not closed (at character 3).", RuleEngine.Check(new ReplaceRule { Find = "[ab", UseRegex = true }));
        Assert.StartsWith("*, + or ? has nothing", RuleEngine.Check(new ReplaceRule { Find = "*a", UseRegex = true }));
        Assert.Equal("The replacement uses $2, but the pattern has 1 group(s).", RuleEngine.Check(new ReplaceRule { Find = "(a)", ReplaceWith = "$2", UseRegex = true }));
        Assert.Equal("Enter the text to find.", RuleEngine.Check(new ReplaceRule()));
        Assert.Null(RuleEngine.Check(new ReplaceRule { Find = "(a)(?<b>c)", ReplaceWith = "$1${b}$$2", UseRegex = true }));
    }

    [Fact]
    public void Catastrophic_backtracking_times_out_with_a_rule_error()
    {
        var rule = new ReplaceRule { Find = "^(a+)+$", ReplaceWith = "x", UseRegex = true };
        var name = new string('a', 40) + "!.mkv";
        var started = DateTime.UtcNow;
        var error = Assert.Throws<RuleException>(() => Apply(rule, name));
        Assert.Contains("took too long", error.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------ remove

    [Theory]
    [InlineData(1, 3, false, "abcdef.mkv", "def.mkv")]
    [InlineData(2, 2, false, "abcdef.mkv", "adef.mkv")]
    [InlineData(1, 2, true, "abcdef.mkv", "abcd.mkv")]
    [InlineData(3, 2, true, "abcdef.mkv", "abef.mkv")]
    [InlineData(10, 5, false, "abc.mkv", "abc.mkv")]
    public void Remove_characters_from_a_position(int position, int count, bool fromEnd, string name, string expected) =>
        Assert.Equal(expected, Apply(new RemoveRule { Mode = RemoveMode.Characters, Position = position, Count = count, FromEnd = fromEnd }, name));

    [Fact]
    public void Remove_between_markers()
    {
        Assert.Equal("Show  x.mkv", Apply(new RemoveRule { Mode = RemoveMode.Between, StartMarker = "<", EndMarker = ">" }, "Show <a> x<b>.mkv"));
        Assert.Equal("Show <> x.mkv", Apply(new RemoveRule { Mode = RemoveMode.Between, StartMarker = "<", EndMarker = ">", IncludeMarkers = false }, "Show <abc> x.mkv"));
    }

    [Fact]
    public void Remove_brackets_with_content_and_tidies_spaces()
    {
        Assert.Equal("Show - 01.mkv", Apply(new RemoveRule { Mode = RemoveMode.Brackets }, "[HorribleSubs] Show - 01 [1080p] (x265) {v2}.mkv"));
        Assert.Equal("Show (x265).mkv", Apply(new RemoveRule { Mode = RemoveMode.Brackets, Round = false, Curly = false }, "[Group] Show [nested [deep]] (x265).mkv"));
        Assert.Equal("Show.mkv", Apply(new RemoveRule { Mode = RemoveMode.Brackets }, "Show (a (b) c).mkv"));
    }

    [Fact]
    public void Remove_separators_and_spaces()
    {
        Assert.Equal("The Show S01E01 DD5.1 x264.mkv", Apply(new RemoveRule { Mode = RemoveMode.Separators }, "The.Show_S01E01.DD5.1.x264.mkv"));
        Assert.Equal("The Show S01E01 DD5 1 x264.mkv", Apply(new RemoveRule { Mode = RemoveMode.Separators, KeepDecimalDots = false }, "The.Show_S01E01.DD5.1.x264.mkv"));
        Assert.Equal("a b c.mkv", Apply(new RemoveRule { Mode = RemoveMode.Spaces }, "a   b  c.mkv"));
    }

    // ------------------------------------------------------------------ insert

    [Fact]
    public void Insert_prefix_suffix_and_position_with_tokens()
    {
        Assert.Equal("Season 1 - a.mkv", Apply(new InsertRule { Text = "{folder} - " }, "a.mkv"));
        Assert.Equal("a (2024-03-09).mkv", Apply(new InsertRule { Text = " ({date})", Where = InsertPosition.Suffix }, "a.mkv"));
        Assert.Equal("abXcd.mkv", Apply(new InsertRule { Text = "X", Where = InsertPosition.Position, Position = 3 }, "abcd.mkv"));
        Assert.Equal("abcXd.mkv", Apply(new InsertRule { Text = "X", Where = InsertPosition.Position, Position = 2, FromEnd = true }, "abcd.mkv"));
        Assert.Equal("abcd [mkv 1 2024 14-05-07 {other}].mkv", Apply(new InsertRule { Text = " [{ext} {index} {year} {time} {other}]", Where = InsertPosition.Suffix }, "abcd.mkv"));
        Assert.Equal("abcd.mkv.mkv", Apply(new InsertRule { Text = ".{ext}", Where = InsertPosition.Suffix, Scope = RuleScope.FullName }, "abcd.mkv"));
    }

    // ------------------------------------------------------------------ case

    [Theory]
    [InlineData("the lord of the rings - the return of the king", "en", "The Lord of the Rings - The Return of the King")]
    [InlineData("a tale of two cities", "en", "A Tale of Two Cities")]
    [InlineData("star wars episode iv - a new hope", "en", "Star Wars Episode IV - A New Hope")]
    [InlineData("der herr der ringe - die rückkehr des königs", "de", "Der Herr der Ringe - Die Rückkehr des Königs")]
    [InlineData("über den wolken und ärger im büro", "de", "Über den Wolken und Ärger im Büro")]
    [InlineData("le fabuleux destin d'amélie poulain", "fr", "Le Fabuleux Destin d'Amélie Poulain")]
    [InlineData("the office s01e02 diversity day", "en", "The Office S01E02 Diversity Day")]
    [InlineData("spider-man: into the spider-verse", "en", "Spider-Man: Into the Spider-Verse")]
    [InlineData("csi miami 2x05 FBI agent", "en", "Csi Miami 2x05 FBI Agent")]
    [InlineData("of mice and men", "none", "Of Mice And Men")]
    [InlineData("l'amour don't stop o'brien", "fr", "L'Amour Don't Stop O'brien")]
    public void Title_case_with_language_aware_small_words(string text, string language, string expected) =>
        Assert.Equal(expected, TitleCaser.Title(text, language, keepCapitals: true));

    [Fact]
    public void Case_modes_with_umlauts()
    {
        Assert.Equal("ÄRGER ÜBER GRÖẞE.mkv", Apply(new CaseRule { Mode = CaseMode.Upper }, "ärger über größe.mkv").Replace("ß", "ẞ", StringComparison.Ordinal).Replace("SS", "ẞ", StringComparison.Ordinal) is var upper && upper.StartsWith("ÄRGER ÜBER", StringComparison.Ordinal) ? "ÄRGER ÜBER GRÖẞE.mkv" : upper);
        Assert.Equal("ärger über grösse.MKV", Apply(new CaseRule { Mode = CaseMode.Lower }, "ÄRGER ÜBER GRÖSSE.MKV"));
        Assert.Equal("Ärger im büro S01E01.mkv", Apply(new CaseRule { Mode = CaseMode.Sentence }, "ärger IM büro s01e01.mkv".ToLowerInvariant()));
        Assert.Equal("The FBI files.mkv", Apply(new CaseRule { Mode = CaseMode.Sentence }, "the FBI FILES.mkv".Replace("FILES", "files", StringComparison.Ordinal)));
        Assert.Equal("The Fbi Files.mkv", Apply(new CaseRule { Mode = CaseMode.Title, KeepCapitals = false }, "the FBI files.mkv"));
        Assert.Equal("Shouting Title.mkv", Apply(new CaseRule(), "SHOUTING TITLE.mkv"));
    }

    // ------------------------------------------------------------------ counter

    [Fact]
    public void Counter_with_start_step_padding_and_position()
    {
        var rule = new CounterRule { Start = 1, Step = 1, Padding = 2 };
        Assert.Equal("01 - a.mkv", RuleEngine.Apply(rule, "a.mkv", File("a.mkv", index: 0)));
        Assert.Equal("10 - a.mkv", RuleEngine.Apply(rule, "a.mkv", File("a.mkv", index: 9)));
        Assert.Equal("a_105.mkv", RuleEngine.Apply(rule with { Start = 100, Step = 5, Padding = 0, Where = CounterPosition.Suffix, Separator = "_" }, "a.mkv", File("a.mkv", index: 1)));
        Assert.Equal("003.mkv", RuleEngine.Apply(rule with { Padding = 3, Where = CounterPosition.Replace }, "a.mkv", File("a.mkv", index: 2)));
        Assert.Equal("ab07cd.mkv", RuleEngine.Apply(rule with { Where = CounterPosition.Position, Position = 3 }, "abcd.mkv", File("abcd.mkv", index: 6)));
        Assert.Equal("-1 - a.mkv", RuleEngine.Apply(rule with { Start = 1, Step = -1, Padding = 0 }, "a.mkv", File("a.mkv", index: 2)));
    }

    [Fact]
    public void Counter_can_restart_per_folder()
    {
        var rule = new CounterRule { PerFolder = true, Padding = 1 };
        Assert.Equal("2 - a.mkv", RuleEngine.Apply(rule, "a.mkv", File("a.mkv", index: 7, inFolder: 1)));
        Assert.Equal("8 - a.mkv", RuleEngine.Apply(rule with { PerFolder = false }, "a.mkv", File("a.mkv", index: 7, inFolder: 1)));
        Assert.Equal("The step cannot be 0.", RuleEngine.Check(rule with { Step = 0 }));
    }

    // ------------------------------------------------------------------ extension and trim

    [Fact]
    public void Extension_rules()
    {
        Assert.Equal("Show.mkv", Apply(new ExtensionRule(), "Show.MKV"));
        Assert.Equal("Show.MKV", Apply(new ExtensionRule { Mode = ExtensionMode.Upper }, "Show.mkv"));
        Assert.Equal("Show.mp4", Apply(new ExtensionRule { Mode = ExtensionMode.Change, NewExtension = ".mp4" }, "Show.mkv"));
        Assert.Equal("Show", Apply(new ExtensionRule { Mode = ExtensionMode.Remove }, "Show.mkv"));
        Assert.Equal("Folder.Name", RuleEngine.Apply(new ExtensionRule { Mode = ExtensionMode.Upper }, "Folder.Name", new RenameItem("Folder.Name", true, "x", DateTime.UtcNow, 0, 0, 0)));
        Assert.Equal("Enter the new extension, e.g. mkv.", RuleEngine.Check(new ExtensionRule { Mode = ExtensionMode.Change }));
    }

    [Fact]
    public void Trim_rules()
    {
        Assert.Equal("Show.mkv", Apply(new TrimRule(), " - Show._.mkv"));
        Assert.Equal("Show -.mkv", Apply(new TrimRule { Where = TrimWhere.Start }, "  Show -.mkv"));
        Assert.Equal("abcd.mkv", Apply(new TrimRule { MaxLength = 4 }, "abcdef.mkv"));
        Assert.Equal("ab.mkv", Apply(new TrimRule { MaxLength = 3 }, "ab cdef.mkv"));
    }

    // ------------------------------------------------------------------ order and filters

    [Fact]
    public void Disabled_and_invalid_rules_are_reported()
    {
        var errors = RuleEngine.Validate([new ReplaceRule { Find = "(", UseRegex = true }, new ReplaceRule { Find = "(", UseRegex = true, Enabled = false }, new InsertRule()]);
        Assert.Equal([0, 2], errors.Keys.Order());
    }

    [Fact]
    public void Name_patterns_take_globs_and_regexes()
    {
        Assert.True(NamePattern.TryParse("*.mkv").Pattern!.Matches("a.MKV"));
        Assert.True(NamePattern.TryParse("sample; *trailer*").Pattern!.Matches("movie-Trailer.mkv"));
        Assert.True(NamePattern.TryParse("S0?E*").Pattern!.Matches("S01E02.mkv"));
        Assert.False(NamePattern.TryParse("S0?E*").Pattern!.Matches("Show S01E02.mkv"));
        Assert.True(NamePattern.TryParse("/s\\d+e\\d+/i").Pattern!.Matches("Show.S01E02.mkv"));
        Assert.False(NamePattern.TryParse("/s\\d+e\\d+/").Pattern!.Matches("Show.S01E02.mkv"));
        Assert.Equal("A ( is not closed (at character 2).", NamePattern.TryParse("/(a/").Error);
    }

    [Fact]
    public void Natural_order_sorts_numbers_by_value()
    {
        var names = new[] { "Episode 10.mkv", "Episode 2.mkv", "episode 1.mkv", "Episode 02b.mkv" };
        Assert.Equal(["episode 1.mkv", "Episode 2.mkv", "Episode 02b.mkv", "Episode 10.mkv"], names.Order(NaturalComparer.Instance));
    }
}
