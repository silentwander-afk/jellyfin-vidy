using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Vidy.Core.Models;

namespace Jellyfin.Plugin.Vidy.Core;

public enum TmdbErrorKind
{
    NotConfigured,
    InvalidKey,
    RateLimited,
    NotFound,
    Unavailable
}

/// <summary>Errors from TMDB. Messages are safe to show to users and never contain the API key or request URL.</summary>
public sealed class TmdbException : Exception
{
    public TmdbException(TmdbErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public TmdbErrorKind Kind { get; }
}

public sealed record TmdbOptions(string ApiKey, bool IncludeAdult, int TimeoutSeconds, TimeSpan CacheTtl, int MaxCacheEntries);

/// <summary>
/// Thin TMDB v3 client (https://developer.themoviedb.org/). A v3 API key is sent as the api_key query
/// parameter; a v4 "API Read Access Token" (a JWT) is sent as a Bearer header. Both are documented by TMDB.
/// </summary>
public sealed class TmdbClient
{
    public const string BaseUrl = "https://api.themoviedb.org/3/";

    private readonly HttpClient _http;
    private readonly Func<TmdbOptions> _options;
    private readonly TtlCache _cache;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public TmdbClient(HttpClient http, Func<TmdbOptions> options, TtlCache cache)
    {
        _http = http;
        _options = options;
        _cache = cache;
        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri(BaseUrl);
        }
    }

    /// <summary>Longest wait honoured for a 429 Retry-After header.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    private sealed record Hit(SearchResult Result, double Popularity);

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync("configuration", new Dictionary<string, string>(), ct);
            return (true, "Connected to TMDB successfully.");
        }
        catch (TmdbException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        string language,
        bool includeMovies,
        bool includeTv,
        int? year,
        int limit,
        CancellationToken ct)
    {
        var raw = (query ?? string.Empty).Trim();
        if (raw.Length == 0 || (!includeMovies && !includeTv))
        {
            return Array.Empty<SearchResult>();
        }

        limit = Math.Clamp(limit, 1, 100);
        var (q, y) = year.HasValue ? (raw, year) : TitleMatcher.SplitYear(raw);

        var results = await SearchCoreAsync(q, y, language, includeMovies, includeTv, ct);
        if (results.Count == 0 && y.HasValue && !year.HasValue)
        {
            // "Wonder Woman 1984": the trailing number may be part of the title, so retry unfiltered.
            results = await SearchCoreAsync(raw, null, language, includeMovies, includeTv, ct);
        }

        return results.Take(limit).ToList();
    }

    private async Task<List<SearchResult>> SearchCoreAsync(
        string q, int? year, string language, bool movies, bool tv, CancellationToken ct)
    {
        var tasks = new List<Task<IReadOnlyList<Hit>>>();
        if (movies)
        {
            tasks.Add(SearchKindAsync(MediaKind.Movie, q, year, language, ct));
        }

        if (tv)
        {
            tasks.Add(SearchKindAsync(MediaKind.Tv, q, year, language, ct));
        }

        var all = await Task.WhenAll(tasks);
        return all.SelectMany(x => x)
            .GroupBy(h => (h.Result.Kind, h.Result.TmdbId))
            .Select(g => g.First())
            .OrderByDescending(h => h.Result.Score)
            .ThenByDescending(h => h.Popularity)
            .Select(h => h.Result)
            .ToList();
    }

    private Task<IReadOnlyList<Hit>> SearchKindAsync(MediaKind kind, string q, int? year, string lang, CancellationToken ct)
    {
        var o = _options();
        var key = $"search|{kind}|{lang}|{o.IncludeAdult}|{TitleMatcher.Normalize(q)}|{year}";
        return Cached<IReadOnlyList<Hit>>(key, async () =>
        {
            var isMovie = kind == MediaKind.Movie;
            var query = new Dictionary<string, string>
            {
                ["query"] = q,
                ["language"] = lang,
                ["include_adult"] = o.IncludeAdult ? "true" : "false"
            };
            if (year.HasValue)
            {
                query[isMovie ? "year" : "first_air_date_year"] = year.Value.ToString(CultureInfo.InvariantCulture);
            }

            using var doc = await GetAsync(isMovie ? "search/movie" : "search/tv", query, ct);
            var list = new List<Hit>();
            if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = GetInt(item, "id");
                    var title = GetStr(item, isMovie ? "title" : "name");
                    if (id is null || id <= 0 || string.IsNullOrEmpty(title))
                    {
                        continue;
                    }

                    var original = GetStr(item, isMovie ? "original_title" : "original_name");
                    var resultYear = ParseYear(GetStr(item, isMovie ? "release_date" : "first_air_date"));
                    var score = TitleMatcher.Score(q, title, original, year, resultYear);
                    list.Add(new Hit(
                        new SearchResult(
                            kind,
                            id.Value,
                            title,
                            original,
                            resultYear,
                            GetStr(item, "poster_path"),
                            GetStr(item, "backdrop_path"),
                            GetStr(item, "overview"),
                            score),
                        GetDouble(item, "popularity")));
                }
            }

            return (IReadOnlyList<Hit>)list;
        });
    }

    public Task<MovieDetails> GetMovieAsync(int id, string language, CancellationToken ct)
        => Cached($"movie|{id}|{language}", async () =>
        {
            var q = new Dictionary<string, string> { ["language"] = language, ["append_to_response"] = "release_dates" };
            using var doc = await GetAsync($"movie/{id}", q, ct);
            var r = doc.RootElement;
            var date = GetStr(r, "release_date");
            return new MovieDetails(
                id,
                GetStr(r, "title") ?? string.Empty,
                GetStr(r, "original_title"),
                GetStr(r, "overview"),
                date,
                ParseYear(date),
                GetInt(r, "runtime") is > 0 and var rt ? rt : null,
                Genres(r),
                GetStr(r, "poster_path"),
                GetStr(r, "backdrop_path"),
                MovieCertification(r, RegionOf(language)));
        });

    public Task<TvDetails> GetTvAsync(int id, string language, CancellationToken ct)
        => Cached($"tv|{id}|{language}", async () =>
        {
            var q = new Dictionary<string, string> { ["language"] = language, ["append_to_response"] = "content_ratings" };
            using var doc = await GetAsync($"tv/{id}", q, ct);
            var r = doc.RootElement;
            var seasons = new List<SeasonSummary>();
            if (r.TryGetProperty("seasons", out var sa) && sa.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sa.EnumerateArray())
                {
                    var n = GetInt(s, "season_number");
                    if (n is null)
                    {
                        continue;
                    }

                    seasons.Add(new SeasonSummary(
                        n.Value,
                        GetStr(s, "name") ?? $"Season {n.Value}",
                        GetInt(s, "episode_count") ?? 0,
                        GetStr(s, "poster_path"),
                        GetStr(s, "air_date")));
                }
            }

            var first = GetStr(r, "first_air_date");
            return new TvDetails(
                id,
                GetStr(r, "name") ?? string.Empty,
                GetStr(r, "original_name"),
                GetStr(r, "overview"),
                first,
                ParseYear(first),
                Genres(r),
                GetInt(r, "number_of_seasons") ?? seasons.Count(s => s.SeasonNumber > 0),
                GetInt(r, "number_of_episodes") ?? seasons.Sum(s => s.EpisodeCount),
                GetStr(r, "poster_path"),
                GetStr(r, "backdrop_path"),
                TvRating(r, RegionOf(language)),
                seasons.OrderBy(s => s.SeasonNumber).ToList());
        });

    public Task<SeasonDetails> GetSeasonAsync(int tvId, int season, string language, CancellationToken ct)
        => Cached($"season|{tvId}|{season}|{language}", async () =>
        {
            using var doc = await GetAsync($"tv/{tvId}/season/{season}", new Dictionary<string, string> { ["language"] = language }, ct);
            var r = doc.RootElement;
            var eps = new List<EpisodeInfo>();
            if (r.TryGetProperty("episodes", out var ea) && ea.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ea.EnumerateArray())
                {
                    var num = GetInt(e, "episode_number");
                    if (num is null || num < 1)
                    {
                        continue;
                    }

                    eps.Add(new EpisodeInfo(
                        GetInt(e, "season_number") ?? season,
                        num.Value,
                        GetStr(e, "name") ?? $"Episode {num.Value}",
                        GetStr(e, "overview"),
                        GetStr(e, "air_date"),
                        GetInt(e, "runtime") is > 0 and var rt ? rt : null,
                        GetStr(e, "still_path")));
                }
            }

            return new SeasonDetails(
                tvId,
                season,
                GetStr(r, "name") ?? $"Season {season}",
                GetStr(r, "overview"),
                GetStr(r, "poster_path"),
                eps.OrderBy(x => x.EpisodeNumber).ToList());
        });

    private Task<T> Cached<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        var o = _options();
        _cache.MaxEntries = o.MaxCacheEntries;
        return _cache.GetOrAddAsync(key, o.CacheTtl, factory);
    }

    private async Task<JsonDocument> GetAsync(string path, Dictionary<string, string> query, CancellationToken ct)
    {
        var o = _options();
        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            throw new TmdbException(TmdbErrorKind.NotConfigured, "The TMDB API key is not configured. Add it in Dashboard → Plugins → Vidy.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = BuildRequest(path, query, o.ApiKey);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));

                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req, cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TmdbException(TmdbErrorKind.Unavailable, "TMDB did not respond in time.");
                }
                catch (HttpRequestException)
                {
                    throw new TmdbException(TmdbErrorKind.Unavailable, "Could not reach TMDB. Check the server's internet connection.");
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        try
                        {
                            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                        }
                        catch (JsonException)
                        {
                            throw new TmdbException(TmdbErrorKind.Unavailable, "TMDB returned an unreadable response.");
                        }
                    }

                    if ((int)resp.StatusCode == 429 && attempt == 0)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                        if (wait > MaxRetryDelay)
                        {
                            wait = MaxRetryDelay;
                        }

                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, ct);
                        }

                        continue;
                    }

                    throw (int)resp.StatusCode switch
                    {
                        401 or 403 => new TmdbException(TmdbErrorKind.InvalidKey, "TMDB rejected the API key. Check that it is correct."),
                        404 => new TmdbException(TmdbErrorKind.NotFound, "TMDB has no entry for that id."),
                        429 => new TmdbException(TmdbErrorKind.RateLimited, "TMDB rate limit reached. Wait a moment and try again."),
                        var c => new TmdbException(TmdbErrorKind.Unavailable, $"TMDB returned an error (HTTP {c})."),
                    };
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpRequestMessage BuildRequest(string path, Dictionary<string, string> query, string key)
    {
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal) && key.Count(c => c == '.') == 2;
        var parts = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}").ToList();
        if (!bearer)
        {
            parts.Add("api_key=" + Uri.EscapeDataString(key));
        }

        var url = path + (parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (bearer)
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        return req;
    }

    private static string RegionOf(string language)
    {
        var i = language.IndexOf('-');
        return i > 0 && i < language.Length - 1 ? language[(i + 1)..] : "US";
    }

    private static string? MovieCertification(JsonElement root, string region)
    {
        if (!root.TryGetProperty("release_dates", out var rd) || !rd.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var country in results.EnumerateArray())
        {
            if (GetStr(country, "iso_3166_1") != region || !country.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var d in dates.EnumerateArray())
            {
                var c = GetStr(d, "certification");
                if (!string.IsNullOrEmpty(c))
                {
                    return c;
                }
            }
        }

        return null;
    }

    private static string? TvRating(JsonElement root, string region)
    {
        if (!root.TryGetProperty("content_ratings", out var cr) || !cr.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var r in results.EnumerateArray())
        {
            if (GetStr(r, "iso_3166_1") == region)
            {
                return GetStr(r, "rating");
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Genres(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in g.EnumerateArray())
            {
                var name = GetStr(item, "name");
                if (name != null)
                {
                    list.Add(name);
                }
            }
        }

        return list;
    }

    private static string? GetStr(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        return null;
    }

    private static int? GetInt(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static double GetDouble(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d
            : 0;

    private static int? ParseYear(string? date)
        => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0
            ? y
            : null;
}
