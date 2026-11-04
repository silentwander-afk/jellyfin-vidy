using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Vidy;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "b7c2a4e1-5d3f-4a68-9c1e-2f8d6a0b3e47";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Vidy";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Search TMDB from Jellyfin and watch movies and TV through the Vidy embed player, with per-user progress, resume and Continue Watching.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Vidy",
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.config.html"
        };
    }
}
