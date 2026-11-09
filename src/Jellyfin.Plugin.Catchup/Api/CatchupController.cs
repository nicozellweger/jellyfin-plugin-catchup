using System.Globalization;
using System.Net;
using System.Security.Claims;
using Jellyfin.Plugin.Catchup.Services;
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
    private readonly ILogger<CatchupController> _logger;

    public CatchupController(CatchupService service, ILogger<CatchupController> logger)
    {
        _service = service;
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
            ProviderCatchup = DispatcharrClient.IsConfigured,
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

    /// <summary>How to play a programme from its start: a recording item or a catch-up stream.</summary>
    [HttpGet("Resolve")]
    public async Task<ActionResult<object>> Resolve(
        [FromQuery] Guid channelId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        CancellationToken ct)
    {
        var result = await _service.ResolveAsync(channelId, startDate.ToUniversalTime(), endDate.ToUniversalTime(), CurrentUserId(), ct).ConfigureAwait(false);
        if (result is null)
        {
            return NotFound(new { Error = "not_available" });
        }

        return Ok(new
        {
            result.Kind,
            ItemId = result.ItemId?.ToString("N"),
            result.StreamPath,
            result.DurationSeconds,
        });
    }

    /// <summary>Catch-up stream (MPEG-TS) with Range support. The unguessable token is the authorisation,
    /// because media players often cannot add auth headers to a stream URL.</summary>
    [HttpGet("Stream/{token}")]
    [AllowAnonymous]
    public async Task Stream([FromRoute] string token, CancellationToken ct)
    {
        string? url;
        try
        {
            url = await _service.GetPlaybackUrlAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Catch-up] Could not start catch-up session");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (url is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var range = Request.Headers.Range.ToString();
        var upstream = await _service.Backend.OpenStreamAsync(url, range, ct).ConfigureAwait(false);
        if (upstream.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // session expired -> renew once
            upstream.Dispose();
            _service.ForgetPlaybackUrl(token);
            url = await _service.GetPlaybackUrlAsync(token, ct).ConfigureAwait(false);
            if (url is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            upstream = await _service.Backend.OpenStreamAsync(url, range, ct).ConfigureAwait(false);
        }

        using (upstream)
        {
            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp2t";
            if (upstream.Content.Headers.ContentLength is { } len)
            {
                Response.ContentLength = len;
            }

            if (upstream.Content.Headers.ContentRange is { } cr)
            {
                Response.Headers.ContentRange = cr.ToString();
            }

            Response.Headers.AcceptRanges = "bytes";
            if (!upstream.IsSuccessStatusCode)
            {
                return;
            }

            await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                await body.CopyToAsync(Response.Body, 81920, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // client stopped or seeked
            }
            catch (IOException)
            {
                // client disconnected
            }
        }
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
