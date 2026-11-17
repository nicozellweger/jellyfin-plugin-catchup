using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Catchup.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>
/// Catch-up straight from an Xtream Codes account (what most IPTV providers offer): channels and
/// their archive from <c>player_api.php</c>, past programmes from <c>get_simple_data_table</c>, and
/// the stream from the <c>/timeshift/</c> URL. No middleware needed.
/// <para>
/// Channels are matched to Jellyfin by name, because an M3U playlist carries no number that would
/// equal Xtream's own. The account's credentials are part of the stream URL, as the protocol wants it;
/// they stay on the server and are kept out of the log.
/// </para>
/// </summary>
public sealed class XtreamClient : ICatchupBackend
{
    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GuideCacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxGuideChannels = 200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XtreamClient> _logger;
    private readonly ConcurrentDictionary<Guid, int> _streamIds = new();
    private readonly ConcurrentDictionary<int, (DateTime At, IReadOnlyList<BackendProgramme> Items)> _guide = new();
    private readonly SemaphoreSlim _guideLock = new(1, 1);

    private (DateTime At, string For, IReadOnlyList<BackendChannel> Items, Func<DateTime, DateTime> ToServerTime)? _channels;

    public XtreamClient(IHttpClientFactory httpClientFactory, ILogger<XtreamClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string BaseUrl => Config.XtreamUrl.Trim().TrimEnd('/');

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.XtreamUrl) && !string.IsNullOrWhiteSpace(Config.XtreamUsername) && !string.IsNullOrWhiteSpace(Config.XtreamPassword);

    /// <summary>Key of a channel inside this backend (programmes carry the same key).</summary>
    private static string Key(int streamId) => "xc:" + streamId.ToString(CultureInfo.InvariantCulture);

    private static Guid UuidFor(int streamId) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("xtream-stream:" + streamId.ToString(CultureInfo.InvariantCulture))));

    private static string Api(string action) =>
        $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(Config.XtreamUsername)}&password={Uri.EscapeDataString(Config.XtreamPassword)}" +
        (action.Length > 0 ? "&action=" + action : string.Empty);

    private HttpClient CreateClient() => _httpClientFactory.CreateClient(DispatcharrClient.HttpClientName);

    public async Task<IReadOnlyList<BackendChannel>> GetChannelsAsync(CancellationToken ct)
    {
        var account = BaseUrl + "|" + Config.XtreamUsername;
        if (_channels is { } cached && cached.For == account && DateTime.UtcNow - cached.At < ChannelCacheTtl)
        {
            return cached.Items;
        }

        using var client = CreateClient();
        var toServerTime = await ReadServerClockAsync(client, ct).ConfigureAwait(false);

        using var response = await client.GetAsync(Api("get_live_streams"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var items = new List<BackendChannel>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!TryInt(el, "stream_id", out var streamId))
                {
                    continue;
                }

                TryInt(el, "tv_archive", out var archive);
                TryInt(el, "tv_archive_duration", out var days);
                var uuid = UuidFor(streamId);
                _streamIds[uuid] = streamId;
                items.Add(new BackendChannel(
                    uuid,
                    Key(streamId),
                    el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    archive == 1 && days > 0,
                    days));
            }
        }

        _channels = (DateTime.UtcNow, account, items, toServerTime);
        _logger.LogInformation("[Catch-up] Xtream: {Count} channels, {Archive} with catch-up", items.Count, items.Count(c => c.IsCatchup));
        return items;
    }

    /// <summary>
    /// The timeshift URL wants the start in the provider's local time. Its time zone name is used when
    /// this system knows it (correct across daylight-saving changes), else the offset it reports now.
    /// </summary>
    private async Task<Func<DateTime, DateTime>> ReadServerClockAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(Api(string.Empty), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("server_info", out var info))
            {
                if (info.TryGetProperty("timezone", out var tz) && tz.GetString() is { Length: > 0 } id)
                {
                    try
                    {
                        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                        return utc => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
                    }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                    {
                    }
                }

                if (info.TryGetProperty("time_now", out var nowEl)
                    && DateTime.TryParseExact(nowEl.GetString(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                    && TryLong(info, "timestamp_now", out var unix))
                {
                    var offset = local - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    var rounded = TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes / 15) * 15);
                    return utc => utc + rounded;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug("[Catch-up] Xtream server clock not readable ({Error}), assuming UTC", ex.GetType().Name);
        }

        return utc => utc;
    }

    public async Task<IReadOnlyList<BackendProgramme>> GetGuideAsync(int prevDays, IReadOnlyCollection<BackendChannel> channels, CancellationToken ct)
    {
        // one request per channel, so only for channels Jellyfin actually has
        var wanted = channels.Where(c => _streamIds.ContainsKey(c.Uuid)).Take(MaxGuideChannels).ToList();
        var result = new List<BackendProgramme>();
        await _guideLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var client = CreateClient();
            foreach (var channel in wanted)
            {
                var streamId = _streamIds[channel.Uuid];
                if (!_guide.TryGetValue(streamId, out var cached) || DateTime.UtcNow - cached.At > GuideCacheTtl)
                {
                    cached = (DateTime.UtcNow, await FetchGuideAsync(client, streamId, ct).ConfigureAwait(false));
                    _guide[streamId] = cached;
                }

                result.AddRange(cached.Items);
            }
        }
        finally
        {
            _guideLock.Release();
        }

        var floor = DateTime.UtcNow.AddDays(-Math.Max(1, prevDays));
        return result.Where(p => p.EndUtc >= floor).ToList();
    }

    private async Task<IReadOnlyList<BackendProgramme>> FetchGuideAsync(HttpClient client, int streamId, CancellationToken ct)
    {
        var list = new List<BackendProgramme>();
        try
        {
            using var response = await client.GetAsync(
                Api("get_simple_data_table") + "&stream_id=" + streamId.ToString(CultureInfo.InvariantCulture),
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("epg_listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var el in listings.EnumerateArray())
            {
                if (!TryLong(el, "start_timestamp", out var start) || !TryLong(el, "stop_timestamp", out var stop) || stop <= start)
                {
                    continue;
                }

                var title = Decode(el, "title");
                if (title.Length == 0)
                {
                    continue;
                }

                var description = Decode(el, "description");
                list.Add(new BackendProgramme(
                    Key(streamId),
                    DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime,
                    DateTimeOffset.FromUnixTimeSeconds(stop).UtcDateTime,
                    title,
                    null,
                    description.Length > 0 ? description : null,
                    null));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogDebug("[Catch-up] Xtream guide for stream {Stream} not readable ({Error})", streamId, ex.GetType().Name);
        }

        return list;
    }

    public async Task<BackendSession> CreateSessionAsync(Guid channelUuid, DateTime startUtc, int durationMinutes, CancellationToken ct)
    {
        if (!_streamIds.TryGetValue(channelUuid, out var streamId))
        {
            await GetChannelsAsync(ct).ConfigureAwait(false);
            if (!_streamIds.TryGetValue(channelUuid, out streamId))
            {
                throw new HttpRequestException("Unknown Xtream channel");
            }
        }

        var toServerTime = _channels?.ToServerTime ?? (utc => utc);
        var start = toServerTime(startUtc).ToString("yyyy-MM-dd':'HH-mm", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/timeshift/{Uri.EscapeDataString(Config.XtreamUsername)}/{Uri.EscapeDataString(Config.XtreamPassword)}/" +
                  $"{durationMinutes.ToString(CultureInfo.InvariantCulture)}/{start}/{streamId.ToString(CultureInfo.InvariantCulture)}.ts";
        return new BackendSession(string.Empty, url, DateTime.UtcNow.AddHours(6));
    }

    private static string Decode(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var value) || value.GetString() is not { Length: > 0 } raw)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw)).Trim();
        }
        catch (FormatException)
        {
            return raw.Trim(); // some panels send plain text
        }
    }

    // panels send numbers as numbers or as strings, depending on version
    private static bool TryLong(JsonElement el, string property, out long value)
    {
        value = 0;
        if (!el.TryGetProperty(property, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryInt(JsonElement el, string property, out int value)
    {
        var ok = TryLong(el, property, out var l) && l is >= int.MinValue and <= int.MaxValue;
        value = ok ? (int)l : 0;
        return ok;
    }
}

/// <summary>The backend chosen in the settings. Switching needs no restart.</summary>
public sealed class SelectedBackend : ICatchupBackend
{
    private readonly DispatcharrClient _dispatcharr;
    private readonly XtreamClient _xtream;

    public SelectedBackend(DispatcharrClient dispatcharr, XtreamClient xtream)
    {
        _dispatcharr = dispatcharr;
        _xtream = xtream;
    }

    private ICatchupBackend Current =>
        string.Equals(Plugin.Instance?.Configuration.Backend, "xtream", StringComparison.OrdinalIgnoreCase) ? _xtream : _dispatcharr;

    public bool IsConfigured => Current.IsConfigured;

    public Task<IReadOnlyList<BackendChannel>> GetChannelsAsync(CancellationToken ct) => Current.GetChannelsAsync(ct);

    public Task<IReadOnlyList<BackendProgramme>> GetGuideAsync(int prevDays, IReadOnlyCollection<BackendChannel> channels, CancellationToken ct) =>
        Current.GetGuideAsync(prevDays, channels, ct);

    public Task<BackendSession> CreateSessionAsync(Guid channelUuid, DateTime startUtc, int durationMinutes, CancellationToken ct) =>
        Current.CreateSessionAsync(channelUuid, startUtc, durationMinutes, ct);
}
