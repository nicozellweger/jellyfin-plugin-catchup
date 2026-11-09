using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Catchup.Configuration;

/// <summary>Plugin settings (stored by Jellyfin on the server, never sent to clients).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Base URL of Dispatcharr as seen from the Jellyfin server, e.g. http://dispatcharr:9191.</summary>
    public string DispatcharrUrl { get; set; } = "http://dispatcharr:9191";

    /// <summary>Dispatcharr API key of a standard (non-admin) user.</summary>
    public string DispatcharrApiKey { get; set; } = string.Empty;

    /// <summary>Dispatcharr channel profile whose XMLTV is used for past programmes (empty = default output).</summary>
    public string DispatcharrProfile { get; set; } = string.Empty;

    /// <summary>How far back past programmes are offered (hours). Provider limits still apply.</summary>
    public int LookbackHours { get; set; } = 72;

    /// <summary>Recordings are matched to a programme when they start within this many minutes of it.</summary>
    public int RecordingMatchToleranceMinutes { get; set; } = 15;

    /// <summary>Offer provider catch-up (otherwise only recordings are used for start-over).</summary>
    public bool EnableProviderCatchup { get; set; } = true;
}
