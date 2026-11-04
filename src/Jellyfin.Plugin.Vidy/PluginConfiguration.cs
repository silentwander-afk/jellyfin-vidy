using Jellyfin.Plugin.Vidy.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Vidy;

/// <summary>Everything the admin can change in Dashboard → Plugins → Vidy. Stored by Jellyfin as XML.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // API
    public bool TmdbEnabled { get; set; } = true;

    public string TmdbApiKey { get; set; } = string.Empty;

    // Search
    public bool EnableMovieSearch { get; set; } = true;

    public bool EnableTvSearch { get; set; } = true;

    public int SearchResultLimit { get; set; } = 20;

    public string SearchLanguage { get; set; } = "en-US";

    public string MetadataLanguage { get; set; } = "en-US";

    public bool IncludeAdult { get; set; }

    public int SearchTimeoutSeconds { get; set; } = 10;

    // Playback
    public string AccentColor { get; set; } = "DC2626";

    public bool Autoplay { get; set; } = true;

    public bool ResumePlayback { get; set; } = true;

    public bool AutoNextEpisode { get; set; } = true;

    public bool EpisodeSelector { get; set; }

    public bool NextEpisodeCountdown { get; set; } = true;

    public int CountdownSeconds { get; set; } = 8;

    public bool AllowManualNext { get; set; } = true;

    public string AspectRatio { get; set; } = "16:9";

    // History
    public bool TrackingEnabled { get; set; } = true;

    public int CompletionThreshold { get; set; } = 90;

    public bool AutoMarkWatched { get; set; } = true;

    public bool ContinueWatchingEnabled { get; set; } = true;

    public int ProgressSyncSeconds { get; set; } = 10;

    // Cache
    public bool CacheEnabled { get; set; } = true;

    public int CacheMinutes { get; set; } = 360;

    public int MaxCachedEntries { get; set; } = 500;

    // Web client
    public bool InjectClientScript { get; set; } = true;

    /// <summary>Returns a copy with every value clamped/normalised. Always read settings through this.</summary>
    public PluginConfiguration Validated() => new()
    {
        TmdbEnabled = TmdbEnabled,
        TmdbApiKey = Validation.NormalizeApiKey(TmdbApiKey),
        EnableMovieSearch = EnableMovieSearch,
        EnableTvSearch = EnableTvSearch,
        SearchResultLimit = Validation.ClampInt(SearchResultLimit, 1, 50),
        SearchLanguage = Validation.NormalizeLanguage(SearchLanguage),
        MetadataLanguage = Validation.NormalizeLanguage(MetadataLanguage),
        IncludeAdult = IncludeAdult,
        SearchTimeoutSeconds = Validation.ClampInt(SearchTimeoutSeconds, 2, 60),
        AccentColor = Validation.NormalizeColor(AccentColor),
        Autoplay = Autoplay,
        ResumePlayback = ResumePlayback,
        AutoNextEpisode = AutoNextEpisode,
        EpisodeSelector = EpisodeSelector,
        NextEpisodeCountdown = NextEpisodeCountdown,
        CountdownSeconds = Validation.ClampInt(CountdownSeconds, 1, 60),
        AllowManualNext = AllowManualNext,
        AspectRatio = Validation.NormalizeAspect(AspectRatio),
        TrackingEnabled = TrackingEnabled,
        CompletionThreshold = Validation.ClampInt(CompletionThreshold, 50, 100),
        AutoMarkWatched = AutoMarkWatched,
        ContinueWatchingEnabled = ContinueWatchingEnabled,
        ProgressSyncSeconds = Validation.ClampInt(ProgressSyncSeconds, 5, 120),
        CacheEnabled = CacheEnabled,
        CacheMinutes = Validation.ClampInt(CacheMinutes, 1, 10080),
        MaxCachedEntries = Validation.ClampInt(MaxCachedEntries, 10, 5000),
        InjectClientScript = InjectClientScript
    };

    public TmdbOptions ToTmdbOptions()
    {
        var v = Validated();
        return new TmdbOptions(
            v.TmdbEnabled ? v.TmdbApiKey : string.Empty,
            v.IncludeAdult,
            v.SearchTimeoutSeconds,
            v.CacheEnabled ? TimeSpan.FromMinutes(v.CacheMinutes) : TimeSpan.Zero,
            v.MaxCachedEntries);
    }
}
