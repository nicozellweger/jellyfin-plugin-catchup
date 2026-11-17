using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Catchup.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>A Jellyfin Live TV channel that has catch-up.</summary>
public sealed record CatchupChannel(Guid ChannelId, string Number, string Name, Guid BackendUuid, int CatchupDays);

/// <summary>A past or airing programme offered for start-over.</summary>
public sealed record CatchupProgramme(
    string Id,
    Guid ChannelId,
    string Name,
    string? EpisodeTitle,
    string? Overview,
    DateTime StartDate,
    DateTime EndDate,
    string? ImageUrl,
    Guid? RecordingId,
    bool HasProviderCatchup);

/// <summary>How a programme can be played from its start.</summary>
public sealed record ResolveResult(string Kind, Guid? ItemId, string? StreamPath, double? DurationSeconds, DateTime? ItemStartUtc = null);

/// <summary>Combines Jellyfin channels/recordings with the backend's catch-up capability.</summary>
public sealed class CatchupService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ICatchupBackend _backend;
    private readonly CatchupJobManager _jobs;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly MediaBrowser.Controller.IServerApplicationHost _appHost;
    private readonly ILogger<CatchupService> _logger;

    public CatchupService(
        ILibraryManager libraryManager,
        ILiveTvManager liveTvManager,
        ICatchupBackend backend,
        CatchupJobManager jobs,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        MediaBrowser.Controller.IServerApplicationHost appHost,
        ILogger<CatchupService> logger)
    {
        _libraryManager = libraryManager;
        _liveTvManager = liveTvManager;
        _backend = backend;
        _jobs = jobs;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _appHost = appHost;
        _logger = logger;
    }

    public CatchupJobManager Jobs => _jobs;

    /// <summary>This server as its own ffmpeg reaches it (port and base URL from the server's
    /// configuration; plain HTTP, since a local certificate is rarely valid for 127.0.0.1).</summary>
    public string LocalBaseUrl => _appHost.GetApiUrlForLocalAccess(null, false).TrimEnd('/');

    // a channel's codecs and audio tracks change rarely; a wrong guess only costs a transcode decision
    private static readonly TimeSpan LayoutTtl = TimeSpan.FromDays(7);
    private bool _layoutsLoaded;

    private sealed record StoredLayout(Guid Channel, List<MediaStream> Streams, int? Bitrate, DateTime At);

    private static string LayoutFile => Path.Combine(Config.CacheDirectory, "layouts.json");

    /// <summary>Layouts survive a server restart, so the first start per channel is not slow again.</summary>
    private void LoadLayouts()
    {
        if (_layoutsLoaded)
        {
            return;
        }

        _layoutsLoaded = true;
        try
        {
            if (!File.Exists(LayoutFile))
            {
                return;
            }

            var stored = System.Text.Json.JsonSerializer.Deserialize<List<StoredLayout>>(File.ReadAllText(LayoutFile));
            foreach (var l in stored ?? new List<StoredLayout>())
            {
                if (l.Streams is { Count: > 0 } && l.Streams.Any(m => m.Type == MediaStreamType.Video))
                {
                    _layouts.TryAdd(l.Channel, (l.Streams, l.Bitrate, l.At));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[Catch-up] Stored stream layouts not readable, probing again");
        }
    }

    private void SaveLayouts()
    {
        try
        {
            var stored = _layouts.Select(kv => new StoredLayout(kv.Key, kv.Value.Streams.ToList(), kv.Value.Bitrate, kv.Value.At)).ToList();
            System.IO.Directory.CreateDirectory(Config.CacheDirectory);
            File.WriteAllText(LayoutFile, System.Text.Json.JsonSerializer.Serialize(stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug(ex, "[Catch-up] Could not store stream layouts");
        }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (IReadOnlyList<MediaStream> Streams, int? Bitrate, DateTime At)> _layouts = new();

    /// <summary>
    /// Media source for the Replay channel: a finished recording when there is one (or when the
    /// provider has no catch-up), otherwise the provider catch-up as HLS that Jellyfin remuxes for the client.
    /// </summary>
    public async Task<MediaSourceInfo?> GetPlaybackSourceAsync(Guid channelId, DateTime startUtc, DateTime endUtc, Guid userId, CancellationToken ct)
    {
        var cc = (await GetCatchupChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.ChannelId == channelId);
        var providerOk = cc is not null && startUtc >= DateTime.UtcNow.AddDays(-cc.CatchupDays);
        var finished = endUtc + CatchupJobManager.Tail < DateTime.UtcNow;

        if (finished || !providerOk)
        {
            var recordings = await GetRecordingsAsync(userId, ct).ConfigureAwait(false);
            var title = await FindTitleAsync(channelId, startUtc, ct).ConfigureAwait(false);
            var rec = MatchRecording(recordings, channelId, startUtc, endUtc, title);
            if (rec is not null && _libraryManager.GetItemById(rec.Value) is { } recItem)
            {
                var sources = _mediaSourceManager.GetStaticMediaSources(recItem, false);
                if (sources.Count > 0)
                {
                    return sources[0];
                }
            }
        }

        if (!providerOk)
        {
            return null;
        }

        var job = await _jobs.OpenAsync(cc!.BackendUuid, startUtc, endUtc, userId, null, ct).ConfigureAwait(false);

        // Stream layout (codecs, audio tracks) is a property of the channel, not of the programme:
        // probe it once per channel and reuse it, so later starts answer at once while the
        // download is still connecting. Jellyfin needs it to decide copy vs transcode.
        LoadLayouts();
        if (!_layouts.TryGetValue(cc.BackendUuid, out var layout) || DateTime.UtcNow - layout.At > LayoutTtl)
        {
            var first = await _jobs.GetProbeSegmentAsync(job, ct).ConfigureAwait(false)
                        ?? throw new HttpRequestException("Catch-up stream did not start");
            var probed = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = first, Protocol = MediaProtocol.File },
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false,
                },
                ct).ConfigureAwait(false);
            layout = (probed.MediaStreams, probed.Bitrate, DateTime.UtcNow);
            if (probed.MediaStreams.Any(m => m.Type == MediaStreamType.Video))
            {
                _layouts[cc.BackendUuid] = layout;
                SaveLayouts();
            }
        }

        var info = (MediaStreams: layout.Streams, Bitrate: layout.Bitrate);

        return new MediaSourceInfo
        {
            Name = "Replay",
            Path = $"{LocalBaseUrl}/Catchup/Hls/{job.Id}/index.m3u8",
            Protocol = MediaProtocol.Http,
            Container = "hls",
            Type = MediaSourceType.Default,
            VideoType = VideoType.VideoFile,
            RunTimeTicks = TimeSpan.FromSeconds(job.TotalSeconds).Ticks,
            MediaStreams = info.MediaStreams,
            Bitrate = info.Bitrate,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = false,
            AnalyzeDurationMs = 3000, // streams are already known; keeps seeks fast
            IsRemote = false,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
        };
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Jellyfin channels joined to backend channels by channel number.</summary>
    public async Task<IReadOnlyList<CatchupChannel>> GetCatchupChannelsAsync(CancellationToken ct)
    {
        if (!Config.EnableProviderCatchup || !_backend.IsConfigured)
        {
            return Array.Empty<CatchupChannel>();
        }

        var backend = (await _backend.GetChannelsAsync(ct).ConfigureAwait(false))
            .Where(c => c.IsCatchup && c.CatchupDays > 0 && c.Number.Length > 0)
            .GroupBy(c => c.Number)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<CatchupChannel>();
        foreach (var channel in GetJellyfinChannels())
        {
            var number = DispatcharrClient.NormalizeNumber(channel.Number ?? string.Empty);
            if (backend.TryGetValue(number, out var b))
            {
                result.Add(new CatchupChannel(channel.Id, number, channel.Name, b.Uuid, b.CatchupDays));
            }
        }

        return result;
    }

    /// <summary>Past and currently airing programmes for the given channels in [from, to].</summary>
    public async Task<IReadOnlyList<CatchupProgramme>> GetProgrammesAsync(IReadOnlyCollection<Guid> channelIds, DateTime fromUtc, DateTime toUtc, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var floor = now.AddHours(-Math.Max(1, Config.LookbackHours));
        if (fromUtc < floor)
        {
            fromUtc = floor;
        }

        if (toUtc > now)
        {
            toUtc = now;
        }

        var channels = GetJellyfinChannels().Where(c => channelIds.Count == 0 || channelIds.Contains(c.Id)).ToList();
        var catchup = (await GetCatchupChannelsAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.ChannelId);
        var recordings = await GetRecordingsAsync(userId, ct).ConfigureAwait(false);

        var prevDays = (int)Math.Ceiling((now - fromUtc).TotalDays) + 1;
        var guide = IsBackendUsable ? await _backend.GetGuideAsync(prevDays, ct).ConfigureAwait(false) : Array.Empty<BackendProgramme>();
        var byNumber = guide.ToLookup(p => p.ChannelNumber);

        var list = new List<CatchupProgramme>();
        foreach (var channel in channels)
        {
            var number = DispatcharrClient.NormalizeNumber(channel.Number ?? string.Empty);
            catchup.TryGetValue(channel.Id, out var cc);
            var providerFloor = cc is null ? DateTime.MaxValue : now.AddDays(-cc.CatchupDays);

            foreach (var p in byNumber[number])
            {
                if (p.EndUtc <= fromUtc || p.StartUtc >= toUtc)
                {
                    continue;
                }

                var rec = MatchRecording(recordings, channel.Id, p.StartUtc, p.EndUtc, p.Title);
                var provider = cc is not null && p.StartUtc >= providerFloor;
                if (rec is null && !provider)
                {
                    continue;
                }

                list.Add(new CatchupProgramme(
                    ProgrammeId(channel.Id, p.StartUtc),
                    channel.Id,
                    p.Title,
                    p.SubTitle,
                    p.Description,
                    p.StartUtc,
                    p.EndUtc,
                    p.IconUrl,
                    rec,
                    provider));
            }
        }

        return list.OrderBy(p => p.ChannelId).ThenBy(p => p.StartDate).ToList();
    }

    /// <summary>Recording first, provider catch-up second.</summary>
    public async Task<ResolveResult?> ResolveAsync(Guid channelId, DateTime startUtc, DateTime endUtc, Guid userId, double offsetSeconds, CancellationToken ct)
    {
        var recordings = await GetRecordingsAsync(userId, ct).ConfigureAwait(false);
        var title = await FindTitleAsync(channelId, startUtc, ct).ConfigureAwait(false);
        var rec = MatchRecording(recordings, channelId, startUtc, endUtc, title);
        if (rec is not null)
        {
            return new ResolveResult("recording", rec, null, null, RecordingFileStart(recordings.FirstOrDefault(r => r.Id == rec.Value), startUtc));
        }

        var cc = (await GetCatchupChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.ChannelId == channelId);
        if (cc is null || startUtc < DateTime.UtcNow.AddDays(-cc.CatchupDays))
        {
            return null;
        }

        // starts (or reuses) the download right away, so the first segments are ready when the player asks
        var job = await _jobs.OpenAsync(cc.BackendUuid, startUtc, endUtc, userId, offsetSeconds, ct).ConfigureAwait(false);
        var path = $"Catchup/Hls/{job.Id}/index.m3u8";
        if (offsetSeconds > 0)
        {
            path += "?offset=" + ((int)offsetSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ResolveResult("catchup", null, path, job.TotalSeconds);
    }

    public ICatchupBackend Backend => _backend;

    private bool IsBackendUsable => Config.EnableProviderCatchup && _backend.IsConfigured;

    private static string ProgrammeId(Guid channelId, DateTime startUtc) => $"{channelId:N}_{startUtc:yyyyMMddHHmm}";

    private List<LiveTvChannel> GetJellyfinChannels()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel } })
            .OfType<LiveTvChannel>()
            .ToList();
    }

    private async Task<string?> FindTitleAsync(Guid channelId, DateTime startUtc, CancellationToken ct)
    {
        var channel = GetJellyfinChannels().FirstOrDefault(c => c.Id == channelId);
        if (channel is null || !IsBackendUsable)
        {
            return null;
        }

        var number = DispatcharrClient.NormalizeNumber(channel.Number ?? string.Empty);
        var prevDays = (int)Math.Ceiling((DateTime.UtcNow - startUtc).TotalDays) + 1;
        try
        {
            var guide = await _backend.GetGuideAsync(Math.Max(1, prevDays), ct).ConfigureAwait(false);
            return guide.FirstOrDefault(p => p.ChannelNumber == number && Math.Abs((p.StartUtc - startUtc).TotalMinutes) < 2)?.Title;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Catch-up] Guide lookup failed");
            return null;
        }
    }

    private async Task<IReadOnlyList<BaseItemDto>> GetRecordingsAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var options = new DtoOptions(false) { Fields = new[] { MediaBrowser.Model.Querying.ItemFields.Path } };
            var result = await _liveTvManager.GetRecordingsAsync(new RecordingQuery { UserId = userId, Limit = 2000 }, options).ConfigureAwait(false);
            return result.Items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Catch-up] Could not read recordings");
            return Array.Empty<BaseItemDto>();
        }
    }

    // Jellyfin names recordings "<Title> yyyy_MM_dd_HH_mm_ss[ - <episode>].ts" using the programme start in server-local time.
    private static readonly System.Text.RegularExpressions.Regex FileStart =
        new(@"(\d{4})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static Guid? MatchRecording(IReadOnlyList<BaseItemDto> recordings, Guid channelId, DateTime startUtc, DateTime endUtc, string? title = null)
    {
        var tolerance = TimeSpan.FromMinutes(Math.Max(1, Config.RecordingMatchToleranceMinutes));
        var wanted = Normalize(title);
        foreach (var r in recordings)
        {
            DateTime rs;
            if (r.StartDate is not null && r.ChannelId is not null)
            {
                // in-progress / timer-backed recordings carry channel + times
                if (r.ChannelId != channelId)
                {
                    continue;
                }

                rs = r.StartDate.Value.ToUniversalTime();
                var re = (r.EndDate ?? DateTime.MaxValue).ToUniversalTime();
                if (rs <= startUtc + tolerance && re >= startUtc + TimeSpan.FromMinutes(1))
                {
                    return r.Id;
                }

                continue;
            }

            // completed recordings: programme start from the file name, identity from the title
            // movies are named after the programme, series episodes after the episode (series name holds the programme)
            if (wanted.Length == 0 || r.Path is null || (Normalize(r.Name) != wanted && Normalize(r.SeriesName) != wanted))
            {
                continue;
            }

            var m = FileStart.Match(Path.GetFileName(r.Path));
            if (!m.Success)
            {
                continue;
            }

            var local = new DateTime(
                int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeKind.Unspecified);
            rs = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
            if (Math.Abs((rs - startUtc).TotalMinutes) <= tolerance.TotalMinutes)
            {
                return r.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Wall-clock time of position 0 in a recording. Recordings start before the programme
    /// (pre-padding) or in the middle of it, so "20 minutes ago" is not "20 minutes minus
    /// programme start". The file's creation time is when recording really began.
    /// </summary>
    private static DateTime? RecordingFileStart(BaseItemDto? recording, DateTime programmeStartUtc)
    {
        try
        {
            if (recording?.Path is { Length: > 0 } path && File.Exists(path))
            {
                var created = File.GetCreationTimeUtc(path);
                // sanity: within an hour before the programme and not in the future
                if (created > programmeStartUtc.AddHours(-1) && created <= DateTime.UtcNow)
                {
                    return created;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

}
