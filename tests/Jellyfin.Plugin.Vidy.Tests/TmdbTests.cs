using Jellyfin.Plugin.Vidy.Core;
using Jellyfin.Plugin.Vidy.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Vidy.Tests;

public class TmdbTests
{
    private const string MovieSearch = """
        {"results":[
          {"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-30","poster_path":"/m.jpg","popularity":80.5},
          {"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-30","poster_path":"/m.jpg","popularity":80.5},
          {"id":604,"title":"The Matrix Reloaded","original_title":"The Matrix Reloaded","release_date":"2003-05-15","popularity":50}
        ]}
        """;

    private const string TvSearch = """
        {"results":[{"id":9999,"name":"Matrix Show","original_name":"Matrix Show","first_air_date":"2010-01-01","popularity":1}]}
        """;

    private static HttpResponseMessage Route(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/3/search/movie" => T.Ok(MovieSearch),
        "/3/search/tv" => T.Ok(TvSearch),
        _ => T.Status(404)
    };

    [Fact]
    public async Task Search_ranks_dedupes_and_labels_kind()
    {
        var client = T.Client(new FakeHandler(Route));
        var res = await client.SearchAsync("the matrix", "en-US", true, true, null, 20, default);

        Assert.Equal(3, res.Count); // duplicate 603 removed
        Assert.Equal(603, res[0].TmdbId);
        Assert.Equal(MediaKind.Movie, res[0].Kind);
        Assert.Contains(res, r => r.Kind == MediaKind.Tv && r.TmdbId == 9999);
        Assert.Equal(1999, res[0].Year);
    }

    [Fact]
    public async Task Search_respects_type_toggles_and_limit()
    {
        var h = new FakeHandler(Route);
        var client = T.Client(h);
        var moviesOnly = await client.SearchAsync("matrix", "en-US", true, false, null, 20, default);
        Assert.All(moviesOnly, r => Assert.Equal(MediaKind.Movie, r.Kind));
        Assert.DoesNotContain("/3/search/tv", h.Paths);

        var limited = await client.SearchAsync("matrix", "en-US", true, true, null, 1, default);
        Assert.Single(limited);
    }

    [Fact]
    public async Task Search_year_in_query_filters_and_falls_back_when_empty()
    {
        var calls = new List<string>();
        var h = new FakeHandler(r =>
        {
            calls.Add(r.RequestUri!.Query);
            var hasYear = r.RequestUri.Query.Contains("year=1984");
            return r.RequestUri.AbsolutePath == "/3/search/movie" && !hasYear
                ? T.Ok("""{"results":[{"id":1,"title":"Wonder Woman 1984","release_date":"2020-12-16"}]}""")
                : T.Ok("""{"results":[]}""");
        });
        var res = await T.Client(h).SearchAsync("Wonder Woman 1984", "en-US", true, false, null, 20, default);

        Assert.Single(res);
        Assert.Contains(calls, q => q.Contains("year=1984")); // tried with the year first
        Assert.Equal("Wonder Woman 1984", res[0].Title);       // then retried without it
    }

    [Fact]
    public async Task Missing_api_key_is_reported_clearly()
    {
        var client = T.Client(new FakeHandler(Route), key: "  ");
        var ex = await Assert.ThrowsAsync<TmdbException>(() => client.SearchAsync("x", "en-US", true, true, null, 5, default));
        Assert.Equal(TmdbErrorKind.NotConfigured, ex.Kind);
    }

    [Fact]
    public async Task Invalid_key_maps_to_InvalidKey_and_never_leaks_the_key()
    {
        var client = T.Client(new FakeHandler(_ => T.Status(401)));
        var ex = await Assert.ThrowsAsync<TmdbException>(() => client.SearchAsync("matrix", "en-US", true, false, null, 5, default));
        Assert.Equal(TmdbErrorKind.InvalidKey, ex.Kind);
        Assert.DoesNotContain(T.Key, ex.Message);
        Assert.DoesNotContain(T.Key, ex.ToString());
    }

    [Fact]
    public async Task Rate_limit_is_retried_once_then_succeeds()
    {
        var n = 0;
        var h = new FakeHandler(r => ++n == 1 ? T.Status(429) : T.Ok(MovieSearch));
        var res = await T.Client(h).SearchAsync("matrix", "en-US", true, false, null, 5, default);
        Assert.NotEmpty(res);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Persistent_rate_limit_surfaces_RateLimited()
    {
        var client = T.Client(new FakeHandler(_ => T.Status(429)));
        var ex = await Assert.ThrowsAsync<TmdbException>(() => client.SearchAsync("matrix", "en-US", true, false, null, 5, default));
        Assert.Equal(TmdbErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task Network_failure_maps_to_Unavailable()
    {
        var client = T.Client(new FakeHandler(_ => throw new HttpRequestException("boom https://api.themoviedb.org/?api_key=" + T.Key)));
        var ex = await Assert.ThrowsAsync<TmdbException>(() => client.SearchAsync("matrix", "en-US", true, false, null, 5, default));
        Assert.Equal(TmdbErrorKind.Unavailable, ex.Kind);
        Assert.DoesNotContain(T.Key, ex.Message);
    }

    [Fact]
    public async Task Identical_searches_are_cached()
    {
        var h = new FakeHandler(Route);
        var client = T.Client(h);
        await client.SearchAsync("matrix", "en-US", true, false, null, 5, default);
        await client.SearchAsync("MATRIX", "en-US", true, false, null, 5, default);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Cache_disabled_with_zero_ttl_calls_every_time()
    {
        var h = new FakeHandler(Route);
        var client = T.Client(h, ttl: TimeSpan.Zero);
        await client.SearchAsync("matrix", "en-US", true, false, null, 5, default);
        await client.SearchAsync("matrix", "en-US", true, false, null, 5, default);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Api_key_goes_in_query_for_v3_and_header_for_v4_tokens()
    {
        var seen = new List<HttpRequestMessage>();
        var h = new FakeHandler(r => { seen.Add(r); return T.Ok("{}"); });
        await T.Client(h, key: "abc123").TestConnectionAsync(default);
        Assert.Contains("api_key=abc123", seen[0].RequestUri!.Query);

        seen.Clear();
        const string jwt = "eyJhbGciOi.eyJhdWQiOi.sig";
        await T.Client(h, key: jwt).TestConnectionAsync(default);
        Assert.DoesNotContain("api_key", seen[0].RequestUri!.Query);
        Assert.Equal("Bearer", seen[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Tv_and_season_details_are_parsed_with_specials_and_gaps()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/3/tv/1396" => T.Ok("""
                {"name":"Breaking Bad","original_name":"Breaking Bad","first_air_date":"2008-01-20","number_of_seasons":2,"number_of_episodes":3,
                 "genres":[{"name":"Drama"}],
                 "content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-MA"}]},
                 "seasons":[{"season_number":0,"name":"Specials","episode_count":1},{"season_number":2,"name":"Season 2","episode_count":1},{"season_number":1,"name":"Season 1","episode_count":2}]}
                """),
            "/3/tv/1396/season/1" => T.Ok("""{"name":"Season 1","episodes":[{"episode_number":3,"season_number":1,"name":"Three","runtime":47},{"episode_number":1,"season_number":1,"name":"One"}]}"""),
            _ => T.Status(404)
        });
        var client = T.Client(h);

        var tv = await client.GetTvAsync(1396, "en-US", default);
        Assert.Equal("TV-MA", tv.ContentRating);
        Assert.Equal(new[] { 0, 1, 2 }, tv.Seasons.Select(s => s.SeasonNumber).ToArray());
        Assert.Equal(2008, tv.Year);

        var s1 = await client.GetSeasonAsync(1396, 1, "en-US", default);
        Assert.Equal(new[] { 1, 3 }, s1.Episodes.Select(e => e.EpisodeNumber).ToArray()); // gap (no E2) kept, sorted
        Assert.Equal(47, s1.Episodes[1].Runtime);

        var missing = await Assert.ThrowsAsync<TmdbException>(() => client.GetSeasonAsync(1396, 9, "en-US", default));
        Assert.Equal(TmdbErrorKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task Connection_test_reports_success_and_failure()
    {
        var ok = await T.Client(new FakeHandler(_ => T.Ok("{}"))).TestConnectionAsync(default);
        Assert.True(ok.Ok);
        var bad = await T.Client(new FakeHandler(_ => T.Status(401))).TestConnectionAsync(default);
        Assert.False(bad.Ok);
        Assert.Contains("rejected", bad.Message);
    }
}
