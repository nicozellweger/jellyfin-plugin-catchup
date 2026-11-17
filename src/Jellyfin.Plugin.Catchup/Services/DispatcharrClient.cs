using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Jellyfin.Plugin.Catchup.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>A Dispatcharr channel as far as catch-up cares.</summary>
public sealed record BackendChannel(Guid Uuid, string Number, string Name, bool IsCatchup, int CatchupDays);

/// <summary>One programme from the backend guide.</summary>
public sealed record BackendProgramme(string ChannelNumber, DateTime StartUtc, DateTime EndUtc, string Title, string? SubTitle, string? Description, string? IconUrl);

/// <summary>A catch-up playback session handed out by the backend.</summary>
public sealed record BackendSession(string SessionId, string PlaybackUrl, DateTime ExpiresUtc);

/// <summary>Talks to Dispatcharr's REST API and XMLTV output. Results are cached briefly.</summary>
public sealed class DispatcharrClient : ICatchupBackend
{
    public const string HttpClientName = "Catchup.Dispatcharr";

    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GuideCacheTtl = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DispatcharrClient> _logger;
    private readonly SemaphoreSlim _guideLock = new(1, 1);

    private (DateTime At, IReadOnlyList<BackendChannel> Items)? _channels;
    private (DateTime At, int PrevDays, IReadOnlyList<BackendProgramme> Items)? _guide;

    public DispatcharrClient(IHttpClientFactory httpClientFactory, ILogger<DispatcharrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string BaseUrl => Config.DispatcharrUrl.TrimEnd('/');

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.DispatcharrUrl) && !string.IsNullOrWhiteSpace(Config.DispatcharrApiKey);

    private HttpClient CreateClient(bool withApiKey)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        if (withApiKey)
        {
            client.DefaultRequestHeaders.Add("X-API-Key", Config.DispatcharrApiKey);
        }

        return client;
    }

    /// <summary>All channels visible to the configured user, with their catch-up capability.</summary>
    public async Task<IReadOnlyList<BackendChannel>> GetChannelsAsync(CancellationToken ct)
    {
        if (_channels is { } cached && DateTime.UtcNow - cached.At < ChannelCacheTtl)
        {
            return cached.Items;
        }

        using var client = CreateClient(true);
        using var response = await client.GetAsync($"{BaseUrl}/api/channels/channels/?page_size=10000", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("results");
        var items = new List<BackendChannel>();
        foreach (var el in array.EnumerateArray())
        {
            if (!el.TryGetProperty("uuid", out var uuidEl) || !Guid.TryParse(uuidEl.GetString(), out var uuid))
            {
                continue;
            }

            var number = el.TryGetProperty("channel_number", out var n) && n.ValueKind == JsonValueKind.Number
                ? NormalizeNumber(n.GetDouble().ToString(CultureInfo.InvariantCulture))
                : string.Empty;
            items.Add(new BackendChannel(
                uuid,
                number,
                el.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                el.TryGetProperty("is_catchup", out var c) && c.ValueKind == JsonValueKind.True,
                el.TryGetProperty("catchup_days", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0));
        }

        _channels = (DateTime.UtcNow, items);
        return items;
    }

    /// <summary>Guide programmes including the past <paramref name="prevDays"/> days.</summary>
    public async Task<IReadOnlyList<BackendProgramme>> GetGuideAsync(int prevDays, IReadOnlyCollection<BackendChannel> channels, CancellationToken ct)
    {
        if (_guide is { } cached && cached.PrevDays >= prevDays && DateTime.UtcNow - cached.At < GuideCacheTtl)
        {
            return cached.Items;
        }

        await _guideLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_guide is { } again && again.PrevDays >= prevDays && DateTime.UtcNow - again.At < GuideCacheTtl)
            {
                return again.Items;
            }

            var profile = string.IsNullOrWhiteSpace(Config.DispatcharrProfile) ? string.Empty : "/" + Uri.EscapeDataString(Config.DispatcharrProfile);
            using var client = CreateClient(false);
            using var response = await client.GetAsync(
                $"{BaseUrl}/output/epg{profile}?prev_days={prevDays}&days=1",
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var items = ParseXmltv(stream);
            _guide = (DateTime.UtcNow, prevDays, items);
            _logger.LogInformation("[Catch-up] Loaded {Count} guide programmes ({Days} days back)", items.Count, prevDays);
            return items;
        }
        finally
        {
            _guideLock.Release();
        }
    }

    /// <summary>Creates a short-lived catch-up playback session for one programme.</summary>
    public async Task<BackendSession> CreateSessionAsync(Guid channelUuid, DateTime startUtc, int durationMinutes, CancellationToken ct)
    {
        using var client = CreateClient(true);
        var body = new
        {
            channel_uuid = channelUuid,
            start = ArchiveTime.AsIfUtc(startUtc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            duration = durationMinutes,
        };
        using var response = await client.PostAsJsonAsync($"{BaseUrl}/api/catchup/sessions/", body, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Dispatcharr refused the catch-up session ({(int)response.StatusCode}): {Truncate(text, 200)}");
        }

        var dto = await response.Content.ReadFromJsonAsync<SessionDto>(cancellationToken: ct).ConfigureAwait(false)
                  ?? throw new HttpRequestException("Empty catch-up session response");
        var url = dto.PlaybackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? dto.PlaybackUrl : BaseUrl + "/" + dto.PlaybackUrl.TrimStart('/');
        var expires = dto.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(dto.ExpiresAt).UtcDateTime : DateTime.UtcNow.AddMinutes(1);
        return new BackendSession(dto.SessionId, url, expires);
    }

    /// <summary>Opens the upstream catch-up stream, forwarding an optional Range header.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string playbackUrl, string? range, CancellationToken ct)
    {
        var client = CreateClient(false);
        var request = new HttpRequestMessage(HttpMethod.Get, playbackUrl);
        if (!string.IsNullOrEmpty(range) && RangeHeaderValue.TryParse(range, out var parsed))
        {
            request.Headers.Range = parsed;
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    internal static string NormalizeNumber(string number)
    {
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d % 1 == 0 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);
        }

        return number.Trim();
    }

    private static IReadOnlyList<BackendProgramme> ParseXmltv(Stream stream)
    {
        var list = new List<BackendProgramme>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, Async = false });
        while (reader.ReadToFollowing("programme"))
        {
            var channel = NormalizeNumber(reader.GetAttribute("channel") ?? string.Empty);
            if (!TryParseXmltvTime(reader.GetAttribute("start"), out var start) || !TryParseXmltvTime(reader.GetAttribute("stop"), out var stop))
            {
                continue;
            }

            string title = string.Empty;
            string? sub = null, desc = null, icon = null;
            using (var inner = reader.ReadSubtree())
            {
                inner.Read();
                while (inner.Read())
                {
                    if (inner.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (inner.Name)
                    {
                        case "title": title = inner.ReadElementContentAsString(); break;
                        case "sub-title": sub = inner.ReadElementContentAsString(); break;
                        case "desc": desc = inner.ReadElementContentAsString(); break;
                        case "icon": icon = inner.GetAttribute("src"); break;
                    }
                }
            }

            list.Add(new BackendProgramme(channel, start, stop, title, sub, desc, icon));
        }

        return list;
    }

    private static bool TryParseXmltvTime(string? value, out DateTime utc)
    {
        // XMLTV: "20261109075500 +0000"
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        if (v.Length < 14 || !DateTime.TryParseExact(v[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        // Offset as "+0000" / "-0130" (no colon). Missing offset = UTC.
        var offset = TimeSpan.Zero;
        var tz = v[14..].Trim();
        if (tz.Length >= 5 && (tz[0] == '+' || tz[0] == '-')
            && int.TryParse(tz.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            && int.TryParse(tz.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (tz[0] == '-')
            {
                offset = -offset;
            }
        }

        utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        return true;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private sealed class SessionDto
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("playback_url")]
        public string PlaybackUrl { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public long ExpiresAt { get; set; }
    }
}
