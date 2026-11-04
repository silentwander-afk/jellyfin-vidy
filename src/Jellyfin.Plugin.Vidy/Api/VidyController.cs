using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Vidy.Core;
using Jellyfin.Plugin.Vidy.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Vidy.Api;

/// <summary>
/// REST endpoints used by the Vidy browser script and the Dashboard page. The TMDB key never leaves the
/// server: the browser only talks to these endpoints, which require a signed-in Jellyfin user.
/// </summary>
[ApiController]
[Route("Vidy")]
public class VidyController : ControllerBase
{
    private const string UserPolicy = "DefaultAuthorization";
    private const string AdminPolicy = "RequiresElevation";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly TmdbClient _tmdb;
    private readonly HistoryStore _history;
    private readonly TtlCache _cache;
    private readonly ILogger<VidyController> _log;

    public VidyController(TmdbClient tmdb, HistoryStore history, TtlCache cache, ILogger<VidyController> log)
    {
        _tmdb = tmdb;
        _history = history;
        _cache = cache;
        _log = log;
    }

    private static PluginConfiguration Cfg() => (Plugin.Instance?.Configuration ?? new PluginConfiguration()).Validated();

    private ContentResult Json(object value, int status = 200) => new()
    {
        Content = JsonSerializer.Serialize(value, JsonOpts),
        ContentType = "application/json; charset=utf-8",
        StatusCode = status
    };

    private ContentResult Fail(int status, string message) => Json(new { error = message }, status);

    private ContentResult FromTmdb(TmdbException ex)
    {
        _log.LogWarning("Vidy: TMDB request failed ({Kind}): {Message}", ex.Kind, ex.Message);
        var status = ex.Kind switch
        {
            TmdbErrorKind.NotConfigured => 400,
            TmdbErrorKind.NotFound => 404,
            TmdbErrorKind.RateLimited => 429,
            _ => 502
        };
        return Fail(status, ex.Message);
    }

    private bool TryUserId(out string userId)
    {
        userId = User.FindFirst("Jellyfin-UserId")?.Value ?? string.Empty;
        return Guid.TryParse(userId, out _);
    }

    private ContentResult? RequireTmdb(PluginConfiguration cfg)
        => cfg.TmdbEnabled ? null : Fail(400, "TMDB integration is turned off in the Vidy plugin settings.");

    // ---------------------------------------------------------------- settings for the browser

    [HttpGet("Config")]
    [Authorize(Policy = UserPolicy)]
    public ActionResult GetClientConfig()
    {
        var c = Cfg();
        return Json(new
        {
            tmdbConfigured = c.TmdbEnabled && c.TmdbApiKey.Length > 0,
            tmdbEnabled = c.TmdbEnabled,
            movieSearch = c.EnableMovieSearch,
            tvSearch = c.EnableTvSearch,
            accentColor = c.AccentColor,
            tracking = c.TrackingEnabled,
            continueWatching = c.ContinueWatchingEnabled && c.TrackingEnabled
        });
    }

    // ---------------------------------------------------------------- TMDB metadata

    [HttpGet("Search")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> Search([FromQuery] string? q, [FromQuery] int? year, [FromQuery] string? type, CancellationToken ct)
    {
        var c = Cfg();
        var off = RequireTmdb(c);
        if (off != null)
        {
            return off;
        }

        q = (q ?? string.Empty).Trim();
        if (q.Length < 1 || q.Length > 100)
        {
            return Fail(400, "Search text must be between 1 and 100 characters.");
        }

        if (year is < 1888 or > 2100)
        {
            return Fail(400, "Year is out of range.");
        }

        var movies = c.EnableMovieSearch && type != "tv";
        var tv = c.EnableTvSearch && type != "movie";
        try
        {
            var results = await _tmdb.SearchAsync(q, c.SearchLanguage, movies, tv, year, c.SearchResultLimit, ct);
            return Json(new { query = q, results });
        }
        catch (TmdbException ex)
        {
            return FromTmdb(ex);
        }
    }

    [HttpGet("Movie/{id:int}")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetMovie(int id, CancellationToken ct)
    {
        var c = Cfg();
        var off = RequireTmdb(c);
        if (off != null)
        {
            return off;
        }

        if (id <= 0)
        {
            return Fail(400, "Invalid TMDB id.");
        }

        try
        {
            return Json(await _tmdb.GetMovieAsync(id, c.MetadataLanguage, ct));
        }
        catch (TmdbException ex)
        {
            return FromTmdb(ex);
        }
    }

    [HttpGet("Tv/{id:int}")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetTv(int id, CancellationToken ct)
    {
        var c = Cfg();
        var off = RequireTmdb(c);
        if (off != null)
        {
            return off;
        }

        if (id <= 0)
        {
            return Fail(400, "Invalid TMDB id.");
        }

        try
        {
            return Json(await _tmdb.GetTvAsync(id, c.MetadataLanguage, ct));
        }
        catch (TmdbException ex)
        {
            return FromTmdb(ex);
        }
    }

    [HttpGet("Tv/{id:int}/Season/{season:int}")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetSeason(int id, int season, CancellationToken ct)
    {
        var c = Cfg();
        var off = RequireTmdb(c);
        if (off != null)
        {
            return off;
        }

        if (id <= 0 || season < 0)
        {
            return Fail(400, "Invalid TMDB id or season number.");
        }

        try
        {
            return Json(await _tmdb.GetSeasonAsync(id, season, c.MetadataLanguage, ct));
        }
        catch (TmdbException ex)
        {
            return FromTmdb(ex);
        }
    }

    // ---------------------------------------------------------------- player

    /// <summary>
    /// Builds the (validated) Vidy URL for a movie or episode, including the saved resume position, and
    /// works out the next episode across season boundaries. The browser never builds player URLs itself.
    /// </summary>
    [HttpGet("Player")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetPlayer(
        [FromQuery] string? kind,
        [FromQuery] int id,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] bool? forceAutoplay,
        CancellationToken ct)
    {
        var c = Cfg();
        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        if (id <= 0 || (kind != "movie" && kind != "tv"))
        {
            return Fail(400, "A kind ('movie' or 'tv') and a positive TMDB id are required.");
        }

        EpisodeRef? next = null;
        string? nextName = null;
        if (kind == "tv")
        {
            if (season is null or < 0 || episode is null or < 1)
            {
                return Fail(400, "TV playback needs a season (0 or more) and an episode (1 or more).");
            }

            // Episode/season availability and the next episode come from TMDB (cached).
            var off = RequireTmdb(c);
            if (off != null)
            {
                return off;
            }

            try
            {
                var show = await _tmdb.GetTvAsync(id, c.MetadataLanguage, ct);
                var current = await _tmdb.GetSeasonAsync(id, season.Value, c.MetadataLanguage, ct);
                if (!current.Episodes.Any(e => e.EpisodeNumber == episode.Value))
                {
                    return Fail(404, $"Season {season} episode {episode} is not listed on TMDB, so it cannot be played.");
                }

                async Task<IReadOnlyList<int>> EpisodesOf(int s)
                    => (await _tmdb.GetSeasonAsync(id, s, c.MetadataLanguage, ct)).Episodes.Select(e => e.EpisodeNumber).ToList();

                next = await EpisodeNavigator.NextAsync(show.Seasons, season.Value, episode.Value, EpisodesOf);
                if (next != null)
                {
                    var ns = await _tmdb.GetSeasonAsync(id, next.Season, c.MetadataLanguage, ct);
                    nextName = ns.Episodes.FirstOrDefault(e => e.EpisodeNumber == next.Episode)?.Name;
                }
            }
            catch (TmdbException ex)
            {
                return FromTmdb(ex);
            }
        }

        int? resume = null;
        if (c.TrackingEnabled && c.ResumePlayback)
        {
            var key = PlaybackEntry.MakeKey(kind, id, season, episode);
            var entry = (await _history.LoadAsync(uid, ct)).FirstOrDefault(e => e.Key == key);
            if (entry != null && ProgressRules.IsInProgress(entry))
            {
                resume = (int)Math.Floor(entry.PositionSeconds);
            }
        }

        // Vidy's own nextEpisode / autoplayNextEpisode flags are intentionally NOT used: Vidy does not tell the
        // page which episode it moved to, so progress would be saved against the wrong episode. The plugin
        // advances episodes itself (see vidy.js). The optional in-player episode selector has the same caveat.
        var options = new PlayerOptions(
            c.AccentColor,
            Autoplay: c.Autoplay || forceAutoplay == true,
            ProgressSeconds: resume,
            NextEpisode: false,
            EpisodeSelector: kind == "tv" && c.EpisodeSelector,
            AutoplayNextEpisode: false);

        var url = kind == "movie" ? VidyUrl.Movie(id, options) : VidyUrl.Tv(id, season!.Value, episode!.Value, options);
        if (!VidyUrl.IsTrustedUrl(url))
        {
            return Fail(500, "Refusing to use a player URL that is not on a trusted Vidy origin.");
        }

        return Json(new
        {
            url,
            allow = VidyUrl.AllowAttribute,
            aspectRatio = c.AspectRatio,
            startSeconds = resume,
            next = next == null ? null : new { season = next.Season, episode = next.Episode, name = nextName },
            settings = new
            {
                tracking = c.TrackingEnabled,
                autoNextEpisode = c.AutoNextEpisode,
                countdownEnabled = c.NextEpisodeCountdown,
                countdownSeconds = c.CountdownSeconds,
                allowManualNext = c.AllowManualNext,
                syncSeconds = c.ProgressSyncSeconds
            }
        });
    }

    // ---------------------------------------------------------------- history

    public sealed class ProgressDto
    {
        public string? Kind { get; set; }

        public int Id { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public double Position { get; set; }

        public double Duration { get; set; }

        public string? Event { get; set; }

        public string? Title { get; set; }

        public string? PosterPath { get; set; }
    }

    [HttpPost("Progress")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> PostProgress(CancellationToken ct)
    {
        var c = Cfg();
        if (!c.TrackingEnabled)
        {
            return Json(new { saved = false, reason = "Playback tracking is turned off." });
        }

        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        ProgressDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<ProgressDto>(Request.Body, JsonOpts, ct);
        }
        catch (JsonException)
        {
            return Fail(400, "The progress payload was not valid JSON.");
        }

        if (dto == null || dto.Event is not ("timeupdate" or "play" or "pause" or "ended"))
        {
            return Fail(400, "Unknown or missing playback event.");
        }

        try
        {
            var entry = await _history.UpsertAsync(
                uid,
                new ProgressUpdate(dto.Kind ?? string.Empty, dto.Id, dto.Season, dto.Episode, dto.Position, dto.Duration, dto.Event == "ended", dto.Title, dto.PosterPath),
                c.CompletionThreshold,
                c.AutoMarkWatched,
                500,
                ct);
            return Json(new { saved = true, watched = entry.Watched });
        }
        catch (ArgumentException ex)
        {
            return Fail(400, ex.Message);
        }
        catch (IOException ex)
        {
            _log.LogError(ex, "Vidy: could not write playback history.");
            return Fail(500, "The server could not save playback history (disk error). Check the Jellyfin log.");
        }
    }

    [HttpGet("History/Item")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetItemHistory([FromQuery] string? kind, [FromQuery] int id, [FromQuery] int? season, [FromQuery] int? episode, CancellationToken ct)
    {
        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        if (kind != "movie" && kind != "tv")
        {
            return Fail(400, "Kind must be 'movie' or 'tv'.");
        }

        var key = PlaybackEntry.MakeKey(kind, id, season, episode);
        var entry = (await _history.LoadAsync(uid, ct)).FirstOrDefault(e => e.Key == key);
        return Json(new { entry });
    }

    [HttpGet("History/Tv/{id:int}")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetSeriesHistory(int id, CancellationToken ct)
    {
        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        var entries = (await _history.LoadAsync(uid, ct)).Where(e => e.Kind == "tv" && e.TmdbId == id).ToList();
        return Json(new { entries });
    }

    [HttpGet("ContinueWatching")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> GetContinueWatching(CancellationToken ct)
    {
        var c = Cfg();
        if (!c.TrackingEnabled || !c.ContinueWatchingEnabled)
        {
            return Json(new { items = Array.Empty<ContinueItem>(), warnings = Array.Empty<string>() });
        }

        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        var entries = await _history.LoadAsync(uid, ct);
        var result = await ContinueWatchingService.BuildAsync(entries, _tmdb, c.MetadataLanguage, 20, ct);
        return Json(result);
    }

    /// <summary>Clears the signed-in user's own history only.</summary>
    [HttpDelete("History")]
    [Authorize(Policy = UserPolicy)]
    public async Task<ActionResult> ClearMyHistory(CancellationToken ct)
    {
        if (!TryUserId(out var uid))
        {
            return Fail(401, "Could not identify the signed-in Jellyfin user.");
        }

        return Json(new { removed = await _history.ClearUserAsync(uid, ct) });
    }

    // ---------------------------------------------------------------- admin (Dashboard)

    [HttpPost("Admin/TestTmdb")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult> TestTmdb(CancellationToken ct)
    {
        var c = Cfg();
        if (!c.TmdbEnabled)
        {
            return Json(new { ok = false, message = "TMDB integration is turned off." });
        }

        var (ok, message) = await _tmdb.TestConnectionAsync(ct);
        return Json(new { ok, message });
    }

    [HttpPost("Admin/ClearCache")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult ClearCache()
    {
        var count = _cache.Count;
        _cache.Clear();
        return Json(new { cleared = count });
    }

    [HttpDelete("Admin/History")]
    [Authorize(Policy = AdminPolicy)]
    public async Task<ActionResult> ClearAllHistory(CancellationToken ct)
        => Json(new { files = await _history.ClearAllAsync(ct) });

    // ---------------------------------------------------------------- static client files

    [HttpGet("Client/vidy.js")]
    [AllowAnonymous]
    public ActionResult ClientScript() => Resource("vidy.js", "application/javascript; charset=utf-8");

    [HttpGet("Client/vidy.css")]
    [AllowAnonymous]
    public ActionResult ClientStyles() => Resource("vidy.css", "text/css; charset=utf-8");

    private ActionResult Resource(string file, string contentType)
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{file}");
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return File(stream, contentType);
    }
}
