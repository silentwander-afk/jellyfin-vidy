using Jellyfin.Plugin.Vidy.Core;
using Xunit;

namespace Jellyfin.Plugin.Vidy.Tests;

public class SearchTests
{
    [Theory]
    [InlineData("Dune 2021", "Dune", 2021)]
    [InlineData("Dune (2021)", "Dune", 2021)]
    [InlineData("the matrix 1999", "the matrix", 1999)]
    public void Year_is_split_from_the_end_of_a_query(string input, string title, int year)
    {
        var (q, y) = TitleMatcher.SplitYear(input);
        Assert.Equal(title, q);
        Assert.Equal(year, y);
    }

    [Theory]
    [InlineData("1917")]
    [InlineData("Blade Runner 2049")]
    [InlineData("Inception")]
    public void Titles_that_look_like_numbers_are_not_split(string input)
    {
        var (q, y) = TitleMatcher.SplitYear(input);
        Assert.Equal(input, q);
        Assert.Null(y);
    }

    [Fact]
    public void Matching_is_case_and_accent_insensitive()
    {
        Assert.Equal("amelie", TitleMatcher.Normalize("Amélie"));
        Assert.Equal(1.0, TitleMatcher.Score("AMELIE", "Amélie", null, null, null));
    }

    [Fact]
    public void Exact_beats_prefix_beats_contains_beats_fuzzy()
    {
        var exact = TitleMatcher.Score("alien", "Alien", null, null, null);
        var prefix = TitleMatcher.Score("alien", "Alien: Romulus", null, null, null);
        var contains = TitleMatcher.Score("alien", "Close Encounters of the Alien Kind", null, null, null);
        var fuzzy = TitleMatcher.Score("allien", "Alien", null, null, null);
        Assert.True(exact > prefix && prefix > contains && contains > fuzzy && fuzzy > 0);
    }

    [Fact]
    public void Original_title_is_considered()
        => Assert.Equal(1.0, TitleMatcher.Score("gisaengchung", "Parasite", "Gisaengchung", null, null));

    [Fact]
    public void Matching_year_gives_a_bonus()
    {
        var with = TitleMatcher.Score("dune", "Dune", null, 2021, 2021);
        var without = TitleMatcher.Score("dune", "Dune", null, 2021, 1984);
        Assert.True(with > without);
    }

    [Fact]
    public void Levenshtein_distance()
    {
        Assert.Equal(0, TitleMatcher.Levenshtein("abc", "abc"));
        Assert.Equal(1, TitleMatcher.Levenshtein("abc", "abd"));
        Assert.Equal(3, TitleMatcher.Levenshtein("", "abc"));
    }
}
