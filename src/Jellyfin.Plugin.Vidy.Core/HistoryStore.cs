using System.Text.Json;
using Jellyfin.Plugin.Vidy.Core.Models;

namespace Jellyfin.Plugin.Vidy.Core;

/// <summary>
/// Per-user playback history stored as one JSON file per Jellyfin user id inside the plugin's data
/// folder (so it survives browser clears, Jellyfin restarts and plugin updates). Writes are atomic
/// (temp file + move) and serialised by a lock. A corrupt file is renamed to *.corrupt-* rather than lost.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public HistoryStore(string directory)
    {
        _dir = directory;
    }

    public async Task<IReadOnlyList<PlaybackEntry>> LoadAsync(string userId, CancellationToken ct = default)
    {
        var path = PathFor(userId);
        await _lock.WaitAsync(ct);
        try
        {
            return await ReadAsync(path, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PlaybackEntry> UpsertAsync(
        string userId,
        ProgressUpdate update,
        double thresholdPercent,
        bool autoMarkWatched,
        int maxEntries = 500,
        CancellationToken ct = default)
    {
        Validate(update);
        var path = PathFor(userId);
        await _lock.WaitAsync(ct);
        try
        {
            var list = await ReadAsync(path, ct);
            var key = PlaybackEntry.MakeKey(update.Kind, update.TmdbId, update.Season, update.Episode);
            var entry = list.FirstOrDefault(e => e.Key == key);
            if (entry == null)
            {
                entry = new PlaybackEntry
                {
                    Key = key,
                    Kind = update.Kind,
                    TmdbId = update.TmdbId,
                    Season = update.Kind == "tv" ? update.Season : null,
                    Episode = update.Kind == "tv" ? update.Episode : null
                };
                list.Add(entry);
            }

            var duration = update.Duration > 0 ? update.Duration : entry.DurationSeconds;
            var position = update.Position;
            if (duration > 0 && position > duration)
            {
                position = duration;
            }

            // Opening something (position 0) must never mark it watched or wipe real progress.
            if (update.Ended && duration > 0)
            {
                position = duration;
            }

            entry.PositionSeconds = position;
            entry.DurationSeconds = duration;
            entry.LastPlayed = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(update.Title))
            {
                entry.Title = update.Title.Length > 200 ? update.Title[..200] : update.Title;
            }

            if (Validation.IsValidPosterPath(update.PosterPath))
            {
                entry.PosterPath = update.PosterPath;
            }

            if (autoMarkWatched && !entry.Watched && (update.Ended || ProgressRules.IsWatched(position, duration, thresholdPercent)))
            {
                entry.Watched = true;
            }

            if (list.Count > maxEntries)
            {
                list = list.OrderByDescending(e => e.LastPlayed).Take(maxEntries).ToList();
            }

            await WriteAsync(path, list, ct);
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> ClearUserAsync(string userId, CancellationToken ct = default)
    {
        var path = PathFor(userId);
        await _lock.WaitAsync(ct);
        try
        {
            var count = (await ReadAsync(path, ct)).Count;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_dir))
            {
                return 0;
            }

            var files = Directory.GetFiles(_dir, "*.json");
            foreach (var f in files)
            {
                File.Delete(f);
            }

            return files.Length;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Validate(ProgressUpdate u)
    {
        if (u.Kind != "movie" && u.Kind != "tv")
        {
            throw new ArgumentException("Kind must be 'movie' or 'tv'.");
        }

        if (u.TmdbId <= 0)
        {
            throw new ArgumentException("TMDB id must be positive.");
        }

        if (u.Kind == "tv" && (u.Season is null or < 0 || u.Episode is null or < 1))
        {
            throw new ArgumentException("TV progress needs a season (>= 0) and episode (>= 1).");
        }

        if (!double.IsFinite(u.Position) || u.Position < 0 || !double.IsFinite(u.Duration) || u.Duration < 0)
        {
            throw new ArgumentException("Position and duration must be finite, non-negative numbers.");
        }
    }

    private string PathFor(string userId)
    {
        if (!Guid.TryParse(userId, out var g))
        {
            throw new ArgumentException("Invalid user id.", nameof(userId));
        }

        return Path.Combine(_dir, g.ToString("N") + ".json");
    }

    private static async Task<List<PlaybackEntry>> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new List<PlaybackEntry>();
        }

        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<PlaybackEntry>>(fs, Opts, ct) ?? new List<PlaybackEntry>();
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            }
            catch (IOException)
            {
                // Could not preserve it; start fresh rather than fail playback tracking.
            }

            return new List<PlaybackEntry>();
        }
    }

    private async Task WriteAsync(string path, List<PlaybackEntry> list, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, list, Opts, ct);
        }

        File.Move(tmp, path, true);
    }
}
