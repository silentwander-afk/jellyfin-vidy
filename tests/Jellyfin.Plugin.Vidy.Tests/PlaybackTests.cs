using Jellyfin.Plugin.Vidy.Core;
using Jellyfin.Plugin.Vidy.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Vidy.Tests;

public class PlaybackTests
{
    private static readonly IReadOnlyList<SeasonSummary> Seasons = new[]
    {
        new SeasonSummary(0, "Specials", 2, null, null),
        new SeasonSummary(1, "Season 1", 3, null, null),
        new SeasonSummary(2, "Season 2", 0, null, null),   // empty season on TMDB
        new SeasonSummary(3, "Season 3", 2, null, null)
    };

    private static readonly Dictionary<int, int[]> Eps = new()
    {
        [0] = new[] { 1, 2 },
        [1] = new[] { 1, 2, 4 },   // gap: no episode 3
        [2] = Array.Empty<int>(),
        [3] = new[] { 1, 2 }
    };

    private static Task<IReadOnlyList<int>> EpisodesOf(int s) => Task.FromResult<IReadOnlyList<int>>(Eps[s]);

    [Fact]
    public async Task Next_episode_within_a_season_respects_gaps()
        => Assert.Equal(new EpisodeRef(1, 4), await EpisodeNavigator.NextAsync(Seasons, 1, 2, EpisodesOf));

    [Fact]
    public async Task Season_finale_moves_to_the_next_season_that_has_episodes()
        => Assert.Equal(new EpisodeRef(3, 1), await EpisodeNavigator.NextAsync(Seasons, 1, 4, EpisodesOf)); // skips empty season 2

    [Fact]
    public async Task Last_episode_of_the_series_has_no_next()
        => Assert.Null(await EpisodeNavigator.NextAsync(Seasons, 3, 2, EpisodesOf));

    [Fact]
    public async Task Specials_are_skipped_unless_requested()
    {
        Assert.Equal(new EpisodeRef(1, 1), await EpisodeNavigator.NextAsync(Seasons, 0, 2, EpisodesOf));
        var seasonsNoS1 = Seasons.Where(s => s.SeasonNumber != 1).ToList();
        Assert.Equal(new EpisodeRef(0, 2), await EpisodeNavigator.NextAsync(seasonsNoS1, 0, 1, EpisodesOf, includeSpecials: true));
    }

    [Fact]
    public async Task Series_not_starting_at_season_1_episode_1_is_handled()
    {
        var seasons = new[] { new SeasonSummary(5, "S5", 2, null, null), new SeasonSummary(7, "S7", 1, null, null) };
        Task<IReadOnlyList<int>> Of(int s) => Task.FromResult<IReadOnlyList<int>>(s == 5 ? new[] { 10, 11 } : new[] { 3 });
        Assert.Equal(new EpisodeRef(5, 11), await EpisodeNavigator.NextAsync(seasons, 5, 10, Of));
        Assert.Equal(new EpisodeRef(7, 3), await EpisodeNavigator.NextAsync(seasons, 5, 11, Of));
    }

    [Theory]
    [InlineData(90, 100, 90, true)]
    [InlineData(89.9, 100, 90, false)]
    [InlineData(10, 0, 90, false)]
    [InlineData(0, 100, 90, false)]
    public void Completion_threshold(double pos, double dur, double threshold, bool expected)
        => Assert.Equal(expected, ProgressRules.IsWatched(pos, dur, threshold));

    [Fact]
    public void Tiny_positions_are_not_resumable_and_watched_items_are_not_in_progress()
    {
        Assert.False(ProgressRules.IsInProgress(new PlaybackEntry { PositionSeconds = 2 }));
        Assert.True(ProgressRules.IsInProgress(new PlaybackEntry { PositionSeconds = 60 }));
        Assert.False(ProgressRules.IsInProgress(new PlaybackEntry { PositionSeconds = 60, Watched = true }));
    }

    private static TmdbClient FakeTmdb() => T.Client(new FakeHandler(r => r.RequestUri!.AbsolutePath switch
    {
        "/3/tv/1396" => T.Ok("""{"name":"BB","number_of_seasons":2,"seasons":[{"season_number":1,"episode_count":2},{"season_number":2,"episode_count":1}]}"""),
        "/3/tv/1396/season/1" => T.Ok("""{"episodes":[{"episode_number":1,"season_number":1},{"episode_number":2,"season_number":1}]}"""),
        "/3/tv/1396/season/2" => T.Ok("""{"episodes":[{"episode_number":1,"season_number":2}]}"""),
        "/3/tv/77" => T.Ok("""{"name":"Done","seasons":[{"season_number":1,"episode_count":1}]}"""),
        "/3/tv/77/season/1" => T.Ok("""{"episodes":[{"episode_number":1,"season_number":1}]}"""),
        _ => T.Status(404)
    }));

    private static PlaybackEntry E(string kind, int id, int? s, int? e, double pos, double dur, bool watched, int minutesAgo, string title)
        => new()
        {
            Key = PlaybackEntry.MakeKey(kind, id, s, e), Kind = kind, TmdbId = id, Season = s, Episode = e,
            PositionSeconds = pos, DurationSeconds = dur, Watched = watched, Title = title,
            LastPlayed = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
        };

    [Fact]
    public async Task Continue_watching_resumes_unfinished_and_advances_finished_across_seasons()
    {
        var entries = new List<PlaybackEntry>
        {
            E("movie", 603, null, null, 600, 7200, false, 5, "The Matrix"),
            E("movie", 11, null, null, 7000, 7200, true, 1, "Finished Movie"),    // watched → not listed
            E("tv", 1396, 1, 1, 3000, 3000, true, 30, "Breaking Bad"),
            E("tv", 1396, 1, 2, 3000, 3000, true, 10, "Breaking Bad"),            // latest, watched → next is S2E1
            E("tv", 77, 1, 1, 100, 1000, false, 20, "Done Show"),                 // in progress → resume
        };

        var res = await ContinueWatchingService.BuildAsync(entries, FakeTmdb(), "en-US", 20, default);

        Assert.Empty(res.Warnings);
        Assert.Equal(3, res.Items.Count);
        Assert.DoesNotContain(res.Items, i => i.TmdbId == 11);

        var bb = res.Items.Single(i => i.TmdbId == 1396);
        Assert.Equal("next", bb.Action);
        Assert.Equal((2, 1), (bb.Season!.Value, bb.Episode!.Value));
        Assert.Equal("S2E1", bb.Label);

        var done = res.Items.Single(i => i.TmdbId == 77);
        Assert.Equal("resume", done.Action);
        Assert.Equal(10, done.Percent);

        Assert.Equal("resume", res.Items.Single(i => i.TmdbId == 603).Action);
    }

    [Fact]
    public async Task Continue_watching_has_one_entry_per_series_and_hides_finished_series()
    {
        var entries = new List<PlaybackEntry>
        {
            E("tv", 77, 1, 1, 1000, 1000, true, 5, "Done Show"),   // only episode watched, no next → hidden
            E("tv", 1396, 1, 1, 50, 3000, false, 9, "BB"),
            E("tv", 1396, 1, 2, 70, 3000, false, 3, "BB"),         // latest in-progress wins; no duplicate series row
        };
        var res = await ContinueWatchingService.BuildAsync(entries, FakeTmdb(), "en-US", 20, default);

        var item = Assert.Single(res.Items);
        Assert.Equal(1396, item.TmdbId);
        Assert.Equal(2, item.Episode);
    }

    [Fact]
    public async Task Continue_watching_skips_episodes_already_watched_out_of_order()
    {
        var entries = new List<PlaybackEntry>
        {
            E("tv", 1396, 1, 2, 3000, 3000, true, 5, "BB"),
            E("tv", 1396, 2, 1, 3000, 3000, true, 50, "BB"),       // S2E1 was already watched earlier
        };
        var res = await ContinueWatchingService.BuildAsync(entries, FakeTmdb(), "en-US", 20, default);
        Assert.Empty(res.Items); // nothing left after S2E1
    }

    [Fact]
    public async Task Continue_watching_reports_tmdb_failures_as_warnings_instead_of_silently_dropping()
    {
        var failing = T.Client(new FakeHandler(_ => T.Status(500)));
        var entries = new List<PlaybackEntry> { E("tv", 1396, 1, 1, 3000, 3000, true, 5, "BB") };
        var res = await ContinueWatchingService.BuildAsync(entries, failing, "en-US", 20, default);
        Assert.Empty(res.Items);
        Assert.Single(res.Warnings);
    }

    [Fact]
    public async Task Continue_watching_respects_the_limit_and_sorts_by_recency()
    {
        var entries = Enumerable.Range(1, 5).Select(i => E("movie", i, null, null, 60, 600, false, i * 10, "M" + i)).ToList();
        var res = await ContinueWatchingService.BuildAsync(entries, FakeTmdb(), "en-US", 2, default);
        Assert.Equal(new[] { 1, 2 }, res.Items.Select(i => i.TmdbId).ToArray());
    }
}
