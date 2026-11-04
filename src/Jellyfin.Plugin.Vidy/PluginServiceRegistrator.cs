using Jellyfin.Plugin.Vidy.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Vidy;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TtlCache>();

        serviceCollection.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            return new TmdbClient(
                http,
                () => (Plugin.Instance?.Configuration ?? new PluginConfiguration()).ToTmdbOptions(),
                sp.GetRequiredService<TtlCache>());
        });

        serviceCollection.AddSingleton(_ =>
        {
            var dataFolder = Plugin.Instance?.DataFolderPath
                             ?? throw new InvalidOperationException("The Vidy plugin is not initialised yet.");
            return new HistoryStore(Path.Combine(dataFolder, "history"));
        });

        serviceCollection.AddHostedService<ScriptInjectionService>();
    }
}
