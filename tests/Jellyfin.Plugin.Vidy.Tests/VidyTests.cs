using Jellyfin.Plugin.Vidy.Core;
using Xunit;

namespace Jellyfin.Plugin.Vidy.Tests;

public class VidyTests
{
    [Fact]
    public void Movie_and_tv_routes_match_the_documented_format()
    {
        Assert.Equal("https://www.vidy.st/movie/315162", VidyUrl.Movie(315162));
        Assert.Equal("https://www.vidy.st/tv/1396/1/1", VidyUrl.Tv(1396, 1, 1));
    }

    [Fact]
    public void Query_flags_use_the_documented_names()
    {
        var url = VidyUrl.Movie(315162, new PlayerOptions("#dc2626", true, 90));
        Assert.Equal("https://www.vidy.st/movie/315162?color=DC2626&progress=90&autoplay=true", url);

        var tv = VidyUrl.Tv(1396, 1, 1, new PlayerOptions(null, false, null, true, true, true));
        Assert.Equal("https://www.vidy.st/tv/1396/1/1?nextEpisode=true&episodeSelector=true&autoplayNextEpisode=true", tv);
    }

    [Fact]
    public void Episode_flags_are_ignored_for_movies_and_bad_values_dropped()
    {
        var url = VidyUrl.Movie(5, new PlayerOptions("not-a-color", false, 0, true, true, true));
        Assert.Equal("https://www.vidy.st/movie/5", url);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-3, 1, 1)]
    [InlineData(5, -1, 1)]
    [InlineData(5, 1, 0)]
    public void Invalid_ids_are_rejected(int id, int season, int episode)
        => Assert.Throws<ArgumentOutOfRangeException>(() => VidyUrl.Tv(id, season, episode));

    [Fact]
    public void Specials_season_zero_is_allowed()
        => Assert.Equal("https://www.vidy.st/tv/10/0/2", VidyUrl.Tv(10, 0, 2));

    [Theory]
    [InlineData("https://vidy.st/movie/1", true)]
    [InlineData("https://www.vidy.st/tv/1/1/1?x=1", true)]
    [InlineData("http://vidy.st/movie/1", false)]
    [InlineData("https://evil.com/movie/1", false)]
    [InlineData("https://vidy.st.evil.com/movie/1", false)]
    [InlineData("https://user@vidy.st/movie/1", false)]
    [InlineData("https://vidy.st:8443/movie/1", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_trusted_https_vidy_urls_are_accepted(string? url, bool expected)
        => Assert.Equal(expected, VidyUrl.IsTrustedUrl(url));

    [Fact]
    public void Allow_attribute_is_exactly_the_documented_policy()
        => Assert.Equal("encrypted-media; autoplay *; fullscreen *", VidyUrl.AllowAttribute);

    [Theory]
    [InlineData("https://vidy.st", true)]
    [InlineData("https://www.vidy.st", true)]
    [InlineData("https://VIDY.ST", true)]
    [InlineData("https://vidy.st.evil.com", false)]
    [InlineData("http://vidy.st", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void Origin_validation(string? origin, bool expected)
        => Assert.Equal(expected, VidyUrl.IsTrustedOrigin(origin));

    [Fact]
    public void Valid_timeupdate_message_is_parsed()
    {
        var e = PlayerMessageParser.TryParse("https://vidy.st", """{"event":"timeupdate","currentTime":12.5,"duration":3600}""");
        Assert.NotNull(e);
        Assert.Equal("timeupdate", e!.Event);
        Assert.Equal(12.5, e.CurrentTime);
        Assert.Equal(3600, e.Duration);
    }

    [Theory]
    [InlineData("https://evil.com", """{"event":"timeupdate","currentTime":1,"duration":2}""")]   // wrong origin
    [InlineData("https://vidy.st", """{"event":"seek","currentTime":1}""")]                       // unknown event
    [InlineData("https://vidy.st", """{"type":"MEDIA_DATA","data":"abc"}""")]                      // not a player event
    [InlineData("https://vidy.st", "not json")]                                                    // malformed
    [InlineData("https://vidy.st", "[1,2,3]")]                                                     // wrong shape
    [InlineData("https://vidy.st", """{"event":"timeupdate","currentTime":-5}""")]               // negative
    [InlineData("https://vidy.st", """{"event":"timeupdate","currentTime":"7"}""")]              // wrong type
    [InlineData("https://vidy.st", """{"event":"ended","duration":"x"}""")]                      // wrong type
    [InlineData("https://vidy.st", "")]                                                            // empty
    public void Untrusted_or_malformed_messages_are_rejected(string origin, string data)
        => Assert.Null(PlayerMessageParser.TryParse(origin, data));

    [Fact]
    public void Non_string_and_oversized_messages_are_rejected()
    {
        Assert.Null(PlayerMessageParser.TryParse("https://vidy.st", new { @event = "play" }));
        Assert.Null(PlayerMessageParser.TryParse("https://vidy.st", "{\"event\":\"play\",\"pad\":\"" + new string('x', 25000) + "\"}"));
    }

    [Fact]
    public void Play_and_pause_without_numbers_are_accepted_with_null_times()
    {
        var e = PlayerMessageParser.TryParse("https://www.vidy.st", """{"event":"play"}""");
        Assert.NotNull(e);
        Assert.Null(e!.CurrentTime);
    }

    [Fact]
    public void Settings_validation_clamps_and_normalises()
    {
        Assert.Equal(100, Validation.ClampInt(500, 50, 100));
        Assert.Equal(50, Validation.ClampInt(-1, 50, 100));
        Assert.Equal("DC2626", Validation.NormalizeColor("zzz"));
        Assert.Equal("22C55E", Validation.NormalizeColor("#22c55e"));
        Assert.Equal("en-US", Validation.NormalizeLanguage("english"));
        Assert.Equal("fr-CA", Validation.NormalizeLanguage("fr-CA"));
        Assert.Equal("16:9", Validation.NormalizeAspect("5:4"));
        Assert.Equal("21:9", Validation.NormalizeAspect("21:9"));
        Assert.True(Validation.IsValidPosterPath("/abc.jpg"));
        Assert.False(Validation.IsValidPosterPath("https://evil/x.jpg"));
        Assert.False(Validation.IsValidPosterPath("/a/../b"));
    }
}
