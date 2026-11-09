using Jellyfin.Plugin.Catchup.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Catchup;

/// <summary>Catch-up plugin entry point.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Catch-up";

    public override Guid Id => Guid.Parse("6f1c2b4e-8d3a-4c57-9e21-7b5a0c9d4e13");

    public override string Description =>
        "Catch-up / replay for Live TV: past programmes and start-over playback from recordings or an IPTV backend.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
