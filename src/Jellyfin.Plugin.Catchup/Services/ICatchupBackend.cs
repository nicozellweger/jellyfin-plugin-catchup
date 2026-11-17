namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>
/// A source of provider catch-up ("replay"): something that knows which channels keep an archive,
/// what aired on them, and can hand out a stream of a past time span.
/// <para>
/// The rest of the plugin (jobs, HLS, the Replay channel, recordings, the API) does not care where
/// catch-up comes from. Dispatcharr is the first implementation; an Xtream Codes account, a TVHeadend
/// timeshift or anything else can be added by implementing this interface and registering it.
/// Without any configured backend the plugin still offers start-over from recordings.
/// </para>
/// </summary>
public interface ICatchupBackend
{
    /// <summary>Whether the backend has what it needs to be asked (address, credentials).</summary>
    bool IsConfigured { get; }

    /// <summary>Channels the backend knows, with their catch-up capability. Matched to Jellyfin's
    /// Live TV channels by channel number, else by name. <see cref="BackendChannel.Number"/> is also the
    /// key the backend's programmes carry (<see cref="BackendProgramme.ChannelNumber"/>).</summary>
    Task<IReadOnlyList<BackendChannel>> GetChannelsAsync(CancellationToken ct);

    /// <summary>What aired during the last <paramref name="prevDays"/> days (Jellyfin forgets past programmes).
    /// <paramref name="channels"/> are the backend channels Jellyfin has, for backends that ask per channel.</summary>
    Task<IReadOnlyList<BackendProgramme>> GetGuideAsync(int prevDays, IReadOnlyCollection<BackendChannel> channels, CancellationToken ct);

    /// <summary>A stream URL (any container ffmpeg reads) of the channel starting at
    /// <paramref name="startUtc"/> and lasting <paramref name="durationMinutes"/>.</summary>
    Task<BackendSession> CreateSessionAsync(Guid channelUuid, DateTime startUtc, int durationMinutes, CancellationToken ct);
}
