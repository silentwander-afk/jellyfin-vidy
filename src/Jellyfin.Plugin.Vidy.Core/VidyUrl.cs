using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Vidy.Core;

/// <summary>
/// Options mapped to the documented Vidy query flags (color, progress, autoplay, nextEpisode,
/// episodeSelector, autoplayNextEpisode). Source: https://www.vidy.st/ (Embed API reference).
/// </summary>
public sealed record PlayerOptions(
    string? Color = null,
    bool Autoplay = false,
    int? ProgressSeconds = null,
    bool NextEpisode = false,
    bool EpisodeSelector = false,
    bool AutoplayNextEpisode = false);

/// <summary>Builds and validates Vidy embed URLs. Only the documented routes and origins are accepted.</summary>
public static class VidyUrl
{
    public const string BaseUrl = "https://www.vidy.st";

    /// <summary>The exact permission policy documented by Vidy. Do not remove entries.</summary>
    public const string AllowAttribute = "encrypted-media; autoplay *; fullscreen *";

    /// <summary>
    /// Both hosts are trusted because the docs show both (the player page may redirect between them).
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedOrigins = new[] { "https://vidy.st", "https://www.vidy.st" };

    public static bool IsTrustedOrigin(string? origin)
        => origin != null && TrustedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    public static bool IsTrustedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        return u.Scheme == Uri.UriSchemeHttps
               && u.IsDefaultPort
               && string.IsNullOrEmpty(u.UserInfo)
               && IsTrustedOrigin($"{u.Scheme}://{u.Host}");
    }

    public static string Movie(int tmdbId, PlayerOptions? options = null)
    {
        if (tmdbId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tmdbId), "TMDB id must be positive.");
        }

        return $"{BaseUrl}/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}{Query(options, false)}";
    }

    public static string Tv(int tmdbId, int season, int episode, PlayerOptions? options = null)
    {
        if (tmdbId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tmdbId), "TMDB id must be positive.");
        }

        if (season < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(season), "Season cannot be negative.");
        }

        if (episode < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(episode), "Episode must be at least 1.");
        }

        var inv = CultureInfo.InvariantCulture;
        return $"{BaseUrl}/tv/{tmdbId.ToString(inv)}/{season.ToString(inv)}/{episode.ToString(inv)}{Query(options, true)}";
    }

    private static string Query(PlayerOptions? o, bool tv)
    {
        if (o == null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var color = Validation.NormalizeColor(o.Color, string.Empty);
        if (color.Length == 6)
        {
            parts.Add("color=" + color);
        }

        if (o.ProgressSeconds is > 0)
        {
            parts.Add("progress=" + o.ProgressSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (o.Autoplay)
        {
            parts.Add("autoplay=true");
        }

        if (tv)
        {
            if (o.NextEpisode)
            {
                parts.Add("nextEpisode=true");
            }

            if (o.EpisodeSelector)
            {
                parts.Add("episodeSelector=true");
            }

            if (o.AutoplayNextEpisode)
            {
                parts.Add("autoplayNextEpisode=true");
            }
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}

public sealed record PlayerEvent(string Event, double? CurrentTime, double? Duration);

/// <summary>
/// Strict parser for the documented PLAYER_EVENT messages: a JSON string with an "event" of
/// timeupdate | play | pause | ended plus numeric currentTime/duration. Anything else is rejected.
/// The browser client applies the same rules (plus an event.source check) before sending progress.
/// </summary>
public static class PlayerMessageParser
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "timeupdate", "play", "pause", "ended" };

    public const int MaxMessageLength = 20000;

    public static PlayerEvent? TryParse(string? origin, object? data)
    {
        if (!VidyUrl.IsTrustedOrigin(origin) || data is not string text || text.Length == 0 || text.Length > MaxMessageLength)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var ev)
                || ev.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = ev.GetString();
            if (name == null || !Allowed.Contains(name))
            {
                return null;
            }

            if (!TryNumber(root, "currentTime", out var current, out var currentBad)
                || !TryNumber(root, "duration", out var duration, out var durationBad)
                || currentBad
                || durationBad)
            {
                return null;
            }

            return new PlayerEvent(name, current, duration);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A missing field is allowed (null). A present field must be a finite, non-negative number.
    private static bool TryNumber(JsonElement root, string name, out double? value, out bool bad)
    {
        value = null;
        bad = false;
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d) || d < 0)
        {
            bad = true;
            return true;
        }

        value = d;
        return true;
    }
}
