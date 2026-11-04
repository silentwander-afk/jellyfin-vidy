using Jellyfin.Plugin.Vidy.Core.Models;

namespace Jellyfin.Plugin.Vidy.Core;

/// <summary>
/// Builds the Continue Watching list for one user: unfinished movies, and one entry per series that is
/// either "resume this episode" (started, not finished) or "next episode" (the last one is finished and
/// another exists, across season boundaries). One entry per movie/series, so no duplicates.
/// </summary>
public static class ContinueWatchingService
{
    public static async Task<ContinueResult> BuildAsync(
        IReadOnlyList<PlaybackEntry> entries,
        TmdbClient tmdb,
        string language,
        int limit,
        CancellationToken ct)
    {
        var items = new List<ContinueItem>();
        var warnings = new List<string>();

        foreach (var m in entries.Where(e => e.Kind == "movie" && ProgressRules.IsInProgress(e)))
        {
            items.Add(ToItem(m, "resume", "Movie"));
        }

        foreach (var series in entries.Where(e => e.Kind == "tv").GroupBy(e => e.TmdbId))
        {
            var latest = series.OrderByDescending(e => e.LastPlayed).First();
            if (ProgressRules.IsInProgress(latest))
            {
                items.Add(ToItem(latest, "resume", Label(latest.Season, latest.Episode)));
                continue;
            }

            if (!latest.Watched)
            {
                continue;
            }

            try
            {
                var next = await FindNextUnwatchedAsync(series.ToList(), latest, tmdb, language, ct);
                if (next != null)
                {
                    items.Add(new ContinueItem(
                        "tv",
                        latest.TmdbId,
                        next.Season,
                        next.Episode,
                        latest.Title,
                        latest.PosterPath,
                        0,
                        0,
                        0,
                        latest.LastPlayed,
                        "next",
                        Label(next.Season, next.Episode)));
                }
            }
            catch (TmdbException ex)
            {
                warnings.Add($"Could not find the next episode of \"{latest.Title ?? latest.TmdbId.ToString()}\": {ex.Message}");
            }
        }

        return new ContinueResult(
            items.OrderByDescending(i => i.LastPlayed).Take(Math.Max(1, limit)).ToList(),
            warnings);
    }

    private static async Task<EpisodeRef?> FindNextUnwatchedAsync(
        List<PlaybackEntry> series,
        PlaybackEntry latest,
        TmdbClient tmdb,
        string language,
        CancellationToken ct)
    {
        var show = await tmdb.GetTvAsync(latest.TmdbId, language, ct);
        async Task<IReadOnlyList<int>> EpisodesOf(int s)
        {
            var sd = await tmdb.GetSeasonAsync(latest.TmdbId, s, language, ct);
            return sd.Episodes.Select(e => e.EpisodeNumber).ToList();
        }

        var season = latest.Season ?? 1;
        var episode = latest.Episode ?? 1;
        for (var i = 0; i < 200; i++)
        {
            var next = await EpisodeNavigator.NextAsync(show.Seasons, season, episode, EpisodesOf);
            if (next == null)
            {
                return null;
            }

            var seen = series.FirstOrDefault(e => e.Season == next.Season && e.Episode == next.Episode);
            if (seen == null || !seen.Watched)
            {
                return next;
            }

            season = next.Season;
            episode = next.Episode;
        }

        return null;
    }

    private static string Label(int? season, int? episode)
        => season == 0 ? $"Special {episode}" : $"S{season}E{episode}";

    private static ContinueItem ToItem(PlaybackEntry e, string action, string label)
    {
        var pct = e.DurationSeconds > 0 ? (int)Math.Clamp(Math.Round(e.PositionSeconds / e.DurationSeconds * 100), 0, 100) : 0;
        return new ContinueItem(e.Kind, e.TmdbId, e.Season, e.Episode, e.Title, e.PosterPath, e.PositionSeconds, e.DurationSeconds, pct, e.LastPlayed, action, label);
    }
}
