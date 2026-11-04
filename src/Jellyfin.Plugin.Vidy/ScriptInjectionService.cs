using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Vidy;

/// <summary>
/// Jellyfin has no official hook that lets a server plugin add JavaScript to Jellyfin Web, so this
/// service adds one &lt;script&gt; tag to the web client's index.html at startup (re-applied on every
/// start, because Jellyfin updates replace the file). If the web folder is read-only (common in
/// Docker/packaged installs) it logs a warning and the script must be added another way - see README.
/// </summary>
public sealed class ScriptInjectionService : IHostedService
{
    private const string Start = "<!-- vidy-plugin-start -->";
    private const string End = "<!-- vidy-plugin-end -->";
    private const string Tag = Start + "<script defer src=\"../Vidy/Client/vidy.js\"></script>" + End;

    private readonly IApplicationPaths _paths;
    private readonly ILogger<ScriptInjectionService> _log;

    public ScriptInjectionService(IApplicationPaths paths, ILogger<ScriptInjectionService> log)
    {
        _paths = paths;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enabled = (Plugin.Instance?.Configuration ?? new PluginConfiguration()).InjectClientScript;
            var index = Path.Combine(_paths.WebPath, "index.html");
            if (!File.Exists(index))
            {
                _log.LogWarning("Vidy: web client index.html not found at {Path}; the browser script was not injected.", index);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(index);
            var has = html.Contains(Start, StringComparison.Ordinal);
            string? updated = null;

            if (enabled && !has)
            {
                var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    updated = html.Insert(at, Tag);
                }
            }
            else if (!enabled && has)
            {
                var s = html.IndexOf(Start, StringComparison.Ordinal);
                var e = html.IndexOf(End, s, StringComparison.Ordinal);
                if (s >= 0 && e > s)
                {
                    updated = html.Remove(s, e + End.Length - s);
                }
            }

            if (updated != null)
            {
                File.WriteAllText(index, updated);
                _log.LogInformation("Vidy: browser script {Action} in the Jellyfin web client.", enabled ? "injected" : "removed");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                ex,
                "Vidy: could not modify the web client (is the web folder read-only?). Add <script defer src=\"/Vidy/Client/vidy.js\"></script> with a JavaScript-injection plugin instead; see the Vidy README.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
