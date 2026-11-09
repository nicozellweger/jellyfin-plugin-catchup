using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Catchup.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
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
public sealed record ResolveResult(string Kind, Guid? ItemId, string? StreamPath, double? DurationSeconds);

/// <summary>Combines Jellyfin channels/recordings with the backend's catch-up capability.</summary>
public sealed class CatchupService
{
    private static readonly TimeSpan TicketTtl = TimeSpan.FromHours(12);

    private readonly ILibraryManager _libraryManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly DispatcharrClient _backend;
    private readonly ILogger<CatchupService> _logger;
    private readonly ConcurrentDictionary<string, StreamTicket> _tickets = new();

    public CatchupService(ILibraryManager libraryManager, ILiveTvManager liveTvManager, DispatcharrClient backend, ILogger<CatchupService> logger)
    {
        _libraryManager = libraryManager;
        _liveTvManager = liveTvManager;
        _backend = backend;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Jellyfin channels joined to backend channels by channel number.</summary>
    public async Task<IReadOnlyList<CatchupChannel>> GetCatchupChannelsAsync(CancellationToken ct)
    {
        if (!Config.EnableProviderCatchup || !DispatcharrClient.IsConfigured)
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
    public async Task<ResolveResult?> ResolveAsync(Guid channelId, DateTime startUtc, DateTime endUtc, Guid userId, CancellationToken ct)
    {
        var recordings = await GetRecordingsAsync(userId, ct).ConfigureAwait(false);
        var title = await FindTitleAsync(channelId, startUtc, ct).ConfigureAwait(false);
        var rec = MatchRecording(recordings, channelId, startUtc, endUtc, title);
        if (rec is not null)
        {
            return new ResolveResult("recording", rec, null, null);
        }

        var cc = (await GetCatchupChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.ChannelId == channelId);
        if (cc is null || startUtc < DateTime.UtcNow.AddDays(-cc.CatchupDays))
        {
            return null;
        }

        var duration = Math.Max(1, (int)Math.Ceiling((endUtc - startUtc).TotalMinutes));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _tickets[token] = new StreamTicket(cc.BackendUuid, startUtc, duration, DateTime.UtcNow.Add(TicketTtl));
        PruneTickets();
        return new ResolveResult("catchup", null, $"Catchup/Stream/{token}", (endUtc - startUtc).TotalSeconds);
    }

    /// <summary>Upstream playback URL for a stream ticket (creates or renews the backend session).</summary>
    public async Task<string?> GetPlaybackUrlAsync(string token, CancellationToken ct)
    {
        if (!_tickets.TryGetValue(token, out var ticket) || ticket.ExpiresUtc < DateTime.UtcNow)
        {
            return null;
        }

        // Sessions are short-lived for the first request only; reuse a known URL and renew on failure.
        if (ticket.PlaybackUrl is null)
        {
            var session = await _backend.CreateSessionAsync(ticket.BackendUuid, ticket.StartUtc, ticket.DurationMinutes, ct).ConfigureAwait(false);
            ticket.PlaybackUrl = session.PlaybackUrl;
            _logger.LogInformation("[Catch-up] Session {Session} for {Uuid} at {Start:u}", session.SessionId, ticket.BackendUuid, ticket.StartUtc);
        }

        return ticket.PlaybackUrl;
    }

    public void ForgetPlaybackUrl(string token)
    {
        if (_tickets.TryGetValue(token, out var ticket))
        {
            ticket.PlaybackUrl = null;
        }
    }

    public DispatcharrClient Backend => _backend;

    private static bool IsBackendUsable => Config.EnableProviderCatchup && DispatcharrClient.IsConfigured;

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

    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private void PruneTickets()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _tickets)
        {
            if (kv.Value.ExpiresUtc < now)
            {
                _tickets.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed class StreamTicket
    {
        public StreamTicket(Guid backendUuid, DateTime startUtc, int durationMinutes, DateTime expiresUtc)
        {
            BackendUuid = backendUuid;
            StartUtc = startUtc;
            DurationMinutes = durationMinutes;
            ExpiresUtc = expiresUtc;
        }

        public Guid BackendUuid { get; }

        public DateTime StartUtc { get; }

        public int DurationMinutes { get; }

        public DateTime ExpiresUtc { get; }

        public string? PlaybackUrl { get; set; }
    }
}
