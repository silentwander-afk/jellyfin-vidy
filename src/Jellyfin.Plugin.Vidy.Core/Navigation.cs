using Jellyfin.Plugin.Vidy.Core.Models;

namespace Jellyfin.Plugin.Vidy.Core;

public static class ProgressRules
{
    /// <summary>Positions shorter than this are not worth resuming.</summary>
    public const double MinResumeSeconds = 5;

    public static bool IsWatched(double position, double duration, double thresholdPercent)
        => duration > 0 && position >= 0 && position / duration * 100.0 >= thresholdPercent;

    public static bool IsInProgress(PlaybackEntry e) => !e.Watched && e.PositionSeconds >= MinResumeSeconds;
}

public static class EpisodeNavigator
{
    /// <summary>
    /// Finds the episode that follows (season, episode). Uses real episode numbers (so gaps are
    /// respected), skips empty seasons, and moves to the next season that actually has episodes.
    /// Season 0 (specials) is only included when asked for.
    /// </summary>
    public static async Task<EpisodeRef?> NextAsync(
        IReadOnlyList<SeasonSummary> seasons,
        int season,
        int episode,
        Func<int, Task<IReadOnlyList<int>>> episodesOf,
        bool includeSpecials = false)
    {
        var current = await episodesOf(season);
        var later = current.Where(e => e > episode).OrderBy(e => e).ToList();
        if (later.Count > 0)
        {
            return new EpisodeRef(season, later[0]);
        }

        foreach (var s in seasons
                     .Where(s => s.SeasonNumber > season && s.EpisodeCount > 0 && (includeSpecials || s.SeasonNumber > 0))
                     .OrderBy(s => s.SeasonNumber))
        {
            var eps = await episodesOf(s.SeasonNumber);
            if (eps.Count > 0)
            {
                return new EpisodeRef(s.SeasonNumber, eps.Min());
            }
        }

        return null;
    }
}
