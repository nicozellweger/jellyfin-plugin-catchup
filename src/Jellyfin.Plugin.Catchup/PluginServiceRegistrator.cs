using Jellyfin.Plugin.Catchup.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Catchup;

/// <summary>Registers the plugin services with Jellyfin's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(DispatcharrClient.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan; // streams run as long as playback lasts
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Catchup/0.1");
        });
        serviceCollection.AddSingleton<DispatcharrClient>();
        serviceCollection.AddSingleton<CatchupService>();
    }
}
