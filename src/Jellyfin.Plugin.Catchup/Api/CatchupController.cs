using System.Globalization;
using System.Security.Claims;
using Jellyfin.Plugin.Catchup.Channels;
using Jellyfin.Plugin.Catchup.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Api;

/// <summary>Catch-up API for clients (e.g. Moonfin). All JSON endpoints need a signed-in Jellyfin user.</summary>
[ApiController]
[Route("Catchup")]
[Authorize]
public class CatchupController : ControllerBase
{
    private readonly CatchupService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<CatchupController> _logger;

    public CatchupController(CatchupService service, ILibraryManager libraryManager, IChannelManager channelManager, ILogger<CatchupController> logger)
    {
        _service = service;
        _libraryManager = libraryManager;
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <summary>Feature detection: which channels offer provider catch-up and for how many days.</summary>
    [HttpGet("Info")]
    public async Task<ActionResult<object>> GetInfo(CancellationToken ct)
    {
        IReadOnlyList<CatchupChannel> channels;
        string? error = null;
        try
        {
            channels = await _service.GetCatchupChannelsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Catch-up] Backend unavailable");
            channels = Array.Empty<CatchupChannel>();
            error = "backend_unavailable";
        }

        return Ok(new
        {
            Version = typeof(CatchupController).Assembly.GetName().Version?.ToString(),
            Recordings = true,
            ProviderCatchup = _service.Backend.IsConfigured,
            Error = error,
            Channels = channels.Select(c => new { ChannelId = c.ChannelId.ToString("N"), c.Number, c.Name, c.CatchupDays }),
        });
    }

    /// <summary>Past and airing programmes that can be started from the beginning.</summary>
    [HttpGet("Programs")]
    public async Task<ActionResult<object>> GetPrograms(
        [FromQuery] string? channelIds,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        CancellationToken ct)
    {
        var ids = ParseIds(channelIds);
        var to = (endDate ?? DateTime.UtcNow).ToUniversalTime();
        var from = (startDate ?? to.AddHours(-24)).ToUniversalTime();
        var items = await _service.GetProgrammesAsync(ids, from, to, CurrentUserId(), ct).ConfigureAwait(false);
        return Ok(new
        {
            TotalRecordCount = items.Count,
            Items = items.Select(p => new
            {
                p.Id,
                ChannelId = p.ChannelId.ToString("N"),
                p.Name,
                p.EpisodeTitle,
                p.Overview,
                StartDate = p.StartDate.ToString("o", CultureInfo.InvariantCulture),
                EndDate = p.EndDate.ToString("o", CultureInfo.InvariantCulture),
                p.ImageUrl,
                RecordingId = p.RecordingId?.ToString("N"),
                p.HasProviderCatchup,
            }),
        });
    }

    /// <summary>How to play a programme from its start (or from <paramref name="offset"/> seconds into it):
    /// a recording item or a catch-up HLS stream.</summary>
    [HttpGet("Resolve")]
    public async Task<ActionResult<object>> Resolve(
        [FromQuery] Guid channelId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] double? offset,
        CancellationToken ct)
    {
        ResolveResult? result;
        try
        {
            result = await _service.ResolveAsync(channelId, startDate.ToUniversalTime(), endDate.ToUniversalTime(), CurrentUserId(), offset ?? 0, ct).ConfigureAwait(false);
        }
        catch (CatchupBusyException)
        {
            return Conflict(new { Error = "busy" });
        }
        catch (CatchupUnavailableException)
        {
            return NotFound(new { Error = "not_in_archive" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[Catch-up] Provider refused catch-up");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "provider_unavailable" });
        }

        if (result is null)
        {
            return NotFound(new { Error = "not_available" });
        }

        // catch-up also as an item of the Replay channel, so clients can use their normal player
        Guid? itemId = result.ItemId;
        if (result.Kind == "catchup")
        {
            try
            {
                var item = await ReplayChannel.FindOrCreateItemAsync(_libraryManager, _channelManager, channelId, startDate.ToUniversalTime(), endDate.ToUniversalTime(), ct).ConfigureAwait(false);
                itemId = item?.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[Catch-up] Replay item lookup failed");
            }
        }

        return Ok(new
        {
            result.Kind,
            ItemId = itemId?.ToString("N"),
            result.StreamPath,
            result.DurationSeconds,
            result.ItemStartUtc,
        });
    }

    /// <summary>HLS playlist of a catch-up job. The unguessable job id is the authorisation,
    /// because media players often cannot add auth headers to a stream URL.</summary>
    [HttpGet("Hls/{jobId}/index.m3u8")]
    [AllowAnonymous]
    public ActionResult Playlist([FromRoute] string jobId, [FromQuery] double? offset)
    {
        if (!StreamAccessAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var job = _service.Jobs.Get(jobId);
        if (job is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return Content(CatchupJobManager.BuildPlaylist(job, offset), "application/vnd.apple.mpegurl");
    }

    /// <summary>One 6-second segment, produced on demand.</summary>
    [HttpGet("Hls/{jobId}/{segment}")]
    [AllowAnonymous]
    public async Task<ActionResult> Segment([FromRoute] string jobId, [FromRoute] string segment, CancellationToken ct)
    {
        if (!StreamAccessAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var job = _service.Jobs.Get(jobId);
        if (job is null || !TryParseSegment(segment, out var index))
        {
            return NotFound();
        }

        string? path;
        try
        {
            path = await _service.Jobs.GetSegmentAsync(job, index, ct).ConfigureAwait(false);
        }
        catch (CatchupBusyException)
        {
            return Conflict(new { Error = "busy" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[Catch-up] Provider refused catch-up");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (path is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, "video/mp2t", enableRangeProcessing: true);
    }

    /// <summary>
    /// The stream endpoints carry no token because the server's own ffmpeg reads them. That is the
    /// only caller that needs to get in without one: everything else has to be signed in.
    /// </summary>
    private bool StreamAccessAllowed()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return (remote is not null && System.Net.IPAddress.IsLoopback(remote))
               || User.Identity?.IsAuthenticated == true;
    }

    private static bool TryParseSegment(string name, out int index)
    {
        index = -1;
        return name.StartsWith("seg_", StringComparison.Ordinal)
               && name.EndsWith(".ts", StringComparison.Ordinal)
               && int.TryParse(name[4..^3], NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(value, out var id) && id != Guid.Empty)
        {
            return id;
        }

        // API-key requests have no user; like Jellyfin's own endpoints, accept ?userId=
        return Guid.TryParse(Request.Query["userId"].ToString(), out var q) ? q : Guid.Empty;
    }

    private static IReadOnlyCollection<Guid> ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<Guid>();
        }

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
    }
}
