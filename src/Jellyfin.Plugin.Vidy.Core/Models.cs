namespace Jellyfin.Plugin.Vidy.Core.Models;

public enum MediaKind
{
    Movie,
    Tv
}

public sealed record SearchResult(
    MediaKind Kind,
    int TmdbId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? PosterPath,
    string? BackdropPath,
    string? Overview,
    double Score);

public sealed record MovieDetails(
    int TmdbId,
    string Title,
    string? OriginalTitle,
    string? Overview,
    string? ReleaseDate,
    int? Year,
    int? Runtime,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    string? BackdropPath,
    string? ContentRating);

public sealed record SeasonSummary(
    int SeasonNumber,
    string Name,
    int EpisodeCount,
    string? PosterPath,
    string? AirDate);

public sealed record TvDetails(
    int TmdbId,
    string Name,
    string? OriginalName,
    string? Overview,
    string? FirstAirDate,
    int? Year,
    IReadOnlyList<string> Genres,
    int NumberOfSeasons,
    int NumberOfEpisodes,
    string? PosterPath,
    string? BackdropPath,
    string? ContentRating,
    IReadOnlyList<SeasonSummary> Seasons);

public sealed record EpisodeInfo(
    int SeasonNumber,
    int EpisodeNumber,
    string Name,
    string? Overview,
    string? AirDate,
    int? Runtime,
    string? StillPath);

public sealed record SeasonDetails(
    int TmdbId,
    int SeasonNumber,
    string Name,
    string? Overview,
    string? PosterPath,
    IReadOnlyList<EpisodeInfo> Episodes);

public sealed record EpisodeRef(int Season, int Episode);

/// <summary>One stored playback position (a movie, or one episode of a series) for one Jellyfin user.</summary>
public sealed class PlaybackEntry
{
    public string Key { get; set; } = string.Empty;

    /// <summary>"movie" or "tv".</summary>
    public string Kind { get; set; } = "movie";

    public int TmdbId { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public double PositionSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public bool Watched { get; set; }

    public DateTimeOffset LastPlayed { get; set; }

    public string? Title { get; set; }

    public string? PosterPath { get; set; }

    public static string MakeKey(string kind, int tmdbId, int? season, int? episode)
        => kind == "tv" ? $"tv:{tmdbId}:{season}:{episode}" : $"movie:{tmdbId}";
}

public sealed record ProgressUpdate(
    string Kind,
    int TmdbId,
    int? Season,
    int? Episode,
    double Position,
    double Duration,
    bool Ended,
    string? Title,
    string? PosterPath);

public sealed record ContinueItem(
    string Kind,
    int TmdbId,
    int? Season,
    int? Episode,
    string? Title,
    string? PosterPath,
    double PositionSeconds,
    double DurationSeconds,
    int Percent,
    DateTimeOffset LastPlayed,
    string Action,
    string Label);

public sealed record ContinueResult(IReadOnlyList<ContinueItem> Items, IReadOnlyList<string> Warnings);
