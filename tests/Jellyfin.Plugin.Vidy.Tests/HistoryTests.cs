using Jellyfin.Plugin.Vidy.Core;
using Jellyfin.Plugin.Vidy.Core.Models;
using Xunit;

namespace Jellyfin.Plugin.Vidy.Tests;

public class HistoryTests
{
    private static ProgressUpdate Movie(double pos, double dur, bool ended = false, int id = 603)
        => new("movie", id, null, null, pos, dur, ended, "The Matrix", "/m.jpg");

    private static ProgressUpdate Ep(int s, int e, double pos, double dur, bool ended = false)
        => new("tv", 1396, s, e, pos, dur, ended, "Breaking Bad", "/bb.jpg");

    [Fact]
    public async Task Progress_survives_a_new_store_instance_like_a_restart()
    {
        var dir = T.TempDir();
        await new HistoryStore(dir).UpsertAsync(T.UserA, Movie(120, 7200), 90, true);

        var after = await new HistoryStore(dir).LoadAsync(T.UserA);
        var e = Assert.Single(after);
        Assert.Equal(120, e.PositionSeconds);
        Assert.Equal(7200, e.DurationSeconds);
        Assert.False(e.Watched);
    }

    [Fact]
    public async Task Users_have_separate_histories_and_clearing_one_keeps_the_other()
    {
        var store = new HistoryStore(T.TempDir());
        await store.UpsertAsync(T.UserA, Movie(100, 1000), 90, true);
        await store.UpsertAsync(T.UserB, Movie(900, 1000, id: 11), 90, true);

        Assert.Equal(603, (await store.LoadAsync(T.UserA)).Single().TmdbId);
        Assert.Equal(11, (await store.LoadAsync(T.UserB)).Single().TmdbId);

        Assert.Equal(1, await store.ClearUserAsync(T.UserA));
        Assert.Empty(await store.LoadAsync(T.UserA));
        Assert.Single(await store.LoadAsync(T.UserB));
    }

    [Fact]
    public async Task Watched_only_after_the_threshold_or_an_ended_event()
    {
        var store = new HistoryStore(T.TempDir());
        var opened = await store.UpsertAsync(T.UserA, Movie(0, 1000), 90, true);
        Assert.False(opened.Watched); // merely opening must not mark watched

        var at89 = await store.UpsertAsync(T.UserA, Movie(890, 1000), 90, true);
        Assert.False(at89.Watched);

        var at90 = await store.UpsertAsync(T.UserA, Movie(900, 1000), 90, true);
        Assert.True(at90.Watched);

        var ended = await store.UpsertAsync(T.UserA, Ep(1, 1, 10, 2000, ended: true), 90, true);
        Assert.True(ended.Watched);
        Assert.Equal(2000, ended.PositionSeconds);
    }

    [Fact]
    public async Task Threshold_is_configurable_and_auto_mark_can_be_disabled()
    {
        var store = new HistoryStore(T.TempDir());
        Assert.True((await store.UpsertAsync(T.UserA, Movie(600, 1000), 60, true)).Watched);
        Assert.False((await store.UpsertAsync(T.UserA, Movie(1000, 1000, id: 5), 90, false)).Watched);
    }

    [Fact]
    public async Task Watched_status_is_not_lost_when_rewatching_from_the_start()
    {
        var store = new HistoryStore(T.TempDir());
        await store.UpsertAsync(T.UserA, Movie(950, 1000), 90, true);
        var again = await store.UpsertAsync(T.UserA, Movie(30, 1000), 90, true);
        Assert.True(again.Watched);
    }

    [Fact]
    public async Task Same_episode_is_one_entry_and_different_episodes_are_separate()
    {
        var store = new HistoryStore(T.TempDir());
        await store.UpsertAsync(T.UserA, Ep(1, 1, 10, 3000), 90, true);
        await store.UpsertAsync(T.UserA, Ep(1, 1, 20, 3000), 90, true);
        await store.UpsertAsync(T.UserA, Ep(1, 2, 5, 3000), 90, true);
        await store.UpsertAsync(T.UserA, Ep(2, 1, 5, 3000), 90, true);
        var all = await store.LoadAsync(T.UserA);
        Assert.Equal(3, all.Count);
        Assert.Equal(20, all.Single(e => e.Season == 1 && e.Episode == 1).PositionSeconds);
    }

    [Fact]
    public async Task Position_is_capped_at_duration()
    {
        var e = await new HistoryStore(T.TempDir()).UpsertAsync(T.UserA, Movie(5000, 1000), 99, true);
        Assert.Equal(1000, e.PositionSeconds);
    }

    [Theory]
    [InlineData("music", 1, null, null, 1.0, 1.0)]
    [InlineData("movie", 0, null, null, 1.0, 1.0)]
    [InlineData("tv", 5, null, 1, 1.0, 1.0)]
    [InlineData("tv", 5, 1, 0, 1.0, 1.0)]
    [InlineData("movie", 5, null, null, -1.0, 1.0)]
    [InlineData("movie", 5, null, null, double.NaN, 1.0)]
    [InlineData("movie", 5, null, null, 1.0, double.PositiveInfinity)]
    public async Task Invalid_updates_are_rejected(string kind, int id, int? s, int? e, double pos, double dur)
    {
        var store = new HistoryStore(T.TempDir());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(T.UserA, new ProgressUpdate(kind, id, s, e, pos, dur, false, null, null), 90, true));
    }

    [Fact]
    public async Task Bad_user_ids_cannot_escape_the_history_folder()
    {
        var store = new HistoryStore(T.TempDir());
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task Invalid_poster_paths_are_not_stored()
    {
        var store = new HistoryStore(T.TempDir());
        var e = await store.UpsertAsync(T.UserA, new ProgressUpdate("movie", 1, null, null, 10, 100, false, "T", "https://evil/x.png"), 90, true);
        Assert.Null(e.PosterPath);
    }

    [Fact]
    public async Task Corrupt_file_is_set_aside_not_fatal()
    {
        var dir = T.TempDir();
        var store = new HistoryStore(dir);
        await store.UpsertAsync(T.UserA, Movie(10, 100), 90, true);
        File.WriteAllText(Directory.GetFiles(dir, "*.json").Single(), "{ not json");

        Assert.Empty(await store.LoadAsync(T.UserA));
        Assert.Single(Directory.GetFiles(dir, "*.corrupt-*"));
        await store.UpsertAsync(T.UserA, Movie(20, 100), 90, true); // still usable
    }

    [Fact]
    public async Task Entry_count_is_capped_keeping_the_most_recent()
    {
        var store = new HistoryStore(T.TempDir());
        for (var i = 1; i <= 5; i++)
        {
            await store.UpsertAsync(T.UserA, Movie(10, 100, id: i), 90, true, maxEntries: 3);
            await Task.Delay(5);
        }

        var ids = (await store.LoadAsync(T.UserA)).Select(e => e.TmdbId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 3, 4, 5 }, ids);
    }

    [Fact]
    public async Task Concurrent_writes_do_not_lose_entries()
    {
        var store = new HistoryStore(T.TempDir());
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.UpsertAsync(T.UserA, Movie(10, 100, id: i), 90, true)));
        Assert.Equal(20, (await store.LoadAsync(T.UserA)).Count);
    }

    [Fact]
    public async Task Clear_all_removes_every_users_history()
    {
        var store = new HistoryStore(T.TempDir());
        await store.UpsertAsync(T.UserA, Movie(10, 100), 90, true);
        await store.UpsertAsync(T.UserB, Movie(10, 100), 90, true);
        Assert.Equal(2, await store.ClearAllAsync());
        Assert.Empty(await store.LoadAsync(T.UserA));
    }
}
