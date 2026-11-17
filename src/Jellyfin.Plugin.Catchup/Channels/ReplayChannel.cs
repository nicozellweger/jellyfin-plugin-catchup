using System.Globalization;
using System.Security.Claims;
using Jellyfin.Plugin.Catchup.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Channels;

/// <summary>
/// "Replay" channel: TV channel → day → programme. Programmes play like any other video;
/// the media source is resolved when playback starts (recording first, provider catch-up second),
/// so every Jellyfin client gets seeking, resume and transcoding – also away from home.
/// </summary>
public sealed class ReplayChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    public const string ChannelName = "Replay";

    private static int _generation;

    private readonly IServiceProvider _services;

    // ChannelManager builds the channels while it is constructed itself, so everything that
    // (indirectly) needs IChannelManager is resolved lazily on first use.
    public ReplayChannel(IServiceProvider services)
    {
        _services = services;
    }

    private CatchupService _service => _services.GetRequiredService<CatchupService>();

    private IServerConfigurationManager _config => _services.GetRequiredService<IServerConfigurationManager>();

    public string Name => ChannelName;

    public string Description => "Past TV programmes from recordings and provider catch-up.";

    public string DataVersion => "1";

    public string HomePageUrl => "https://github.com/nicozellweger/jellyfin-plugin-catchup";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>Forces Jellyfin to ask the plugin again instead of using its 3-hour folder cache.</summary>
    public static void Invalidate() => Interlocked.Increment(ref _generation);

    /// <inheritdoc />
    public string GetCacheKey(string userId) =>
        (DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(10).Ticks).ToString(CultureInfo.InvariantCulture) + "-" + Volatile.Read(ref _generation).ToString(CultureInfo.InvariantCulture);

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.PremiereDate },
    };

    public bool IsEnabledFor(string userId) => true;

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    // ---- ids -------------------------------------------------------------------------------

    public static string ChannelFolderId(Guid channelId) => "ch_" + channelId.ToString("N");

    public static string DayFolderId(Guid channelId, DateTime localDay) => "day_" + channelId.ToString("N") + "_" + localDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string ProgrammeItemId(Guid channelId, DateTime startUtc, DateTime endUtc) =>
        "p_" + channelId.ToString("N") + "_" + Unix(startUtc).ToString(CultureInfo.InvariantCulture) + "_" + Unix(endUtc).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseProgrammeId(string id, out Guid channelId, out DateTime startUtc, out DateTime endUtc)
    {
        channelId = Guid.Empty;
        startUtc = endUtc = default;
        var parts = id.Split('_');
        if (parts.Length != 4 || parts[0] != "p"
            || !Guid.TryParseExact(parts[1], "N", out channelId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var e))
        {
            return false;
        }

        startUtc = DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
        endUtc = DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime;
        return true;
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // ---- browsing --------------------------------------------------------------------------

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var folder = query.FolderId ?? string.Empty;
        List<ChannelItemInfo> items;
        if (folder.Length == 0)
        {
            items = await GetChannelFoldersAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (folder.StartsWith("ch_", StringComparison.Ordinal) && Guid.TryParseExact(folder[3..], "N", out var channelId))
        {
            items = await GetDayFoldersAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        else if (folder.StartsWith("day_", StringComparison.Ordinal))
        {
            items = await GetDayProgrammesAsync(folder, query.UserId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            items = new List<ChannelItemInfo>();
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<List<ChannelItemInfo>> GetChannelFoldersAsync(CancellationToken ct)
    {
        var channels = await _service.GetCatchupChannelsAsync(ct).ConfigureAwait(false);
        return channels
            .OrderBy(c => double.TryParse(c.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
            .Select(c => new ChannelItemInfo
            {
                Id = ChannelFolderId(c.ChannelId),
                Name = c.Name,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = LocalImageUrl(c.ChannelId),
            })
            .ToList();
    }

    private async Task<List<ChannelItemInfo>> GetDayFoldersAsync(Guid channelId, CancellationToken ct)
    {
        var channel = (await _service.GetCatchupChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.ChannelId == channelId);
        var days = Math.Min(channel?.CatchupDays ?? 1, (int)Math.Ceiling(Math.Max(1, Plugin.Instance?.Configuration.LookbackHours ?? 72) / 24.0));
        var culture = Culture;
        var today = DateTime.Now.Date;
        var list = new List<ChannelItemInfo>();
        for (var i = 0; i <= days; i++)
        {
            var day = today.AddDays(-i);
            var name = i switch
            {
                0 => culture.TwoLetterISOLanguageName == "de" ? "Heute" : "Today",
                1 => culture.TwoLetterISOLanguageName == "de" ? "Gestern" : "Yesterday",
                _ => day.ToString("dddd, d. MMMM", culture),
            };
            list.Add(new ChannelItemInfo
            {
                Id = DayFolderId(channelId, day),
                Name = name,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = LocalImageUrl(channelId),
            });
        }

        return list;
    }

    private async Task<List<ChannelItemInfo>> GetDayProgrammesAsync(string folderId, Guid userId, CancellationToken ct)
    {
        var parts = folderId.Split('_');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[1], "N", out var channelId)
            || !DateTime.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return new List<ChannelItemInfo>();
        }

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var programmes = await _service.GetProgrammesAsync(new[] { channelId }, fromUtc, toUtc, userId, ct).ConfigureAwait(false);
        var culture = Culture;
        return programmes
            .Where(p => TimeZoneInfo.ConvertTimeFromUtc(p.StartDate, TimeZoneInfo.Local).Date == day)
            .OrderByDescending(p => p.StartDate)
            .Select(p =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(p.StartDate, TimeZoneInfo.Local);
                var title = string.IsNullOrWhiteSpace(p.EpisodeTitle) ? p.Name : p.Name + " – " + p.EpisodeTitle;
                return new ChannelItemInfo
                {
                    Id = ProgrammeItemId(channelId, p.StartDate, p.EndDate),
                    Name = local.ToString("HH:mm", culture) + "  " + title,
                    OriginalTitle = p.Name,
                    Overview = p.Overview,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Clip,
                    ImageUrl = p.ImageUrl ?? LocalImageUrl(channelId),
                    PremiereDate = p.StartDate,
                    DateCreated = p.StartDate,
                    RunTimeTicks = (p.EndDate - p.StartDate).Ticks,
                    MediaSources = new List<MediaSourceInfo>(), // resolved at playback time
                };
            })
            .ToList();
    }

    // ---- playback --------------------------------------------------------------------------

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (!TryParseProgrammeId(id, out var channelId, out var startUtc, out var endUtc))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var source = await _service.GetPlaybackSourceAsync(channelId, startUtc, endUtc, CurrentUserId(), cancellationToken).ConfigureAwait(false);
        return source is null ? Array.Empty<MediaSourceInfo>() : new[] { source };
    }

    /// <summary>The Jellyfin item for a programme, creating the channel items on the way (they are created lazily by browsing).</summary>
    public static async Task<BaseItem?> FindOrCreateItemAsync(ILibraryManager libraryManager, IChannelManager channelManager, Guid channelId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var internalId = libraryManager.GetNewItemId("Channel " + ChannelName, typeof(Channel));
        if (libraryManager.GetItemById(internalId) is null)
        {
            await channelManager.GetChannelsInternalAsync(new MediaBrowser.Model.Channels.ChannelQuery()).ConfigureAwait(false);
        }

        var wanted = ProgrammeItemId(channelId, startUtc, endUtc);
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(startUtc, TimeZoneInfo.Local).Date;
        var path = new[] { ChannelFolderId(channelId), DayFolderId(channelId, localDay), wanted };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Guid parent = Guid.Empty;
            BaseItem? found = null;
            foreach (var externalId in path)
            {
                var result = await channelManager.GetChannelItemsInternal(
                    new InternalItemsQuery { ChannelIds = new[] { internalId }, ParentId = parent },
                    new Progress<double>(),
                    ct).ConfigureAwait(false);
                found = result.Items.FirstOrDefault(i => string.Equals(i.ExternalId, externalId, StringComparison.Ordinal));
                if (found is null)
                {
                    break;
                }

                parent = found.Id;
            }

            if (found is not null && found.ExternalId == wanted)
            {
                return found;
            }

            Invalidate(); // folder lists came from Jellyfin's cache – ask the plugin again
        }

        return null;
    }

    private Guid CurrentUserId()
    {
        var context = _services.GetService<IHttpContextAccessor>()?.HttpContext;
        var value = context?.User.FindFirstValue("Jellyfin-UserId") ?? context?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private CultureInfo Culture
    {
        get
        {
            try
            {
                var name = _config.Configuration.UICulture;
                return string.IsNullOrEmpty(name) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }

    private string LocalImageUrl(Guid channelId) => $"{_service.LocalBaseUrl}/Items/{channelId:N}/Images/Primary";
}
