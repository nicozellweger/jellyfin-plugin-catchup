using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Catchup.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>
/// One catch-up programme, exposed as an HLS playlist of fixed 6-second slots.
/// Segments are produced on demand by ffmpeg ("chunks"); a request for a slot that is
/// neither on disk nor about to be written restarts the download right there, so a
/// seek never waits for the whole programme to download.
/// </summary>
public sealed class CatchupJob
{
    internal CatchupJob(string id, Guid backendUuid, DateTime startUtc, DateTime endUtc, Guid ownerUserId, string directory)
    {
        Id = id;
        BackendUuid = backendUuid;
        StartUtc = startUtc;
        EndUtc = endUtc;
        OwnerUserId = ownerUserId;
        Directory = directory;
        LastAccessUtc = DateTime.UtcNow;
        TotalSegments = (int)Math.Ceiling((endUtc - startUtc + CatchupJobManager.Tail).TotalSeconds / CatchupJobManager.SegmentSeconds);
    }

    public string Id { get; }

    public Guid BackendUuid { get; }

    public DateTime StartUtc { get; }

    /// <summary>Programme end (the playlist runs a few minutes longer for overruns).</summary>
    public DateTime EndUtc { get; }

    public Guid OwnerUserId { get; internal set; }

    public string Directory { get; }

    public int TotalSegments { get; }

    public DateTime LastAccessUtc { get; internal set; }

    /// <summary>First segment of the running (or last) ffmpeg chunk.</summary>
    internal int ChunkStart { get; set; } = -1;

    internal Process? Process { get; set; }

    internal string? LastError { get; set; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public bool Downloading
    {
        get
        {
            try
            {
                return Process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public string SegmentPath(int index) => Path.Combine(Directory, SegmentName(index));

    public static string SegmentName(int index) => "seg_" + index.ToString("D5", CultureInfo.InvariantCulture) + ".ts";

    /// <summary>Wall-clock time at which slot <paramref name="index"/> starts.</summary>
    public DateTime SegmentStartUtc(int index) => StartUtc.AddSeconds(index * CatchupJobManager.SegmentSeconds);

    public double SegmentDuration(int index) =>
        index < TotalSegments - 1
            ? CatchupJobManager.SegmentSeconds
            : (EndUtc - StartUtc + CatchupJobManager.Tail).TotalSeconds - (index * CatchupJobManager.SegmentSeconds);

    public double TotalSeconds => (EndUtc - StartUtc + CatchupJobManager.Tail).TotalSeconds;
}

/// <summary>Thrown when the provider connection for catch-up is in use by another viewer.</summary>
public sealed class CatchupBusyException : Exception
{
    public CatchupBusyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Owns all catch-up downloads. Providers allow very few connections, so at most one
/// ffmpeg download runs at a time: a new request of the same viewer takes the
/// connection over, a running download of another viewer is never interrupted.
/// Segments live on disk (configurable, ideally a hard disk), bounded by size and age.
/// </summary>
public sealed class CatchupJobManager : IDisposable
{
    public const int SegmentSeconds = 6;
    public static readonly TimeSpan Tail = TimeSpan.FromMinutes(5);

    private const int RestartAlignSegments = 10;   // provider timeshift works in whole minutes
    private const int ReachSegments = 40;          // this close ahead of the writer we wait instead of restarting
    private static readonly TimeSpan JobTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan OwnerIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LiveDelay = TimeSpan.FromSeconds(20);

    private readonly DispatcharrClient _backend;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<CatchupJobManager> _logger;
    private readonly ConcurrentDictionary<string, CatchupJob> _jobs = new();
    private readonly SemaphoreSlim _connection = new(1, 1);

    public CatchupJobManager(DispatcharrClient backend, IMediaEncoder mediaEncoder, ILogger<CatchupJobManager> logger)
    {
        _backend = backend;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string Root => Config.CacheDirectory;

    public CatchupJob? Get(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.LastAccessUtc = DateTime.UtcNow;
            return job;
        }

        return null;
    }

    /// <summary>
    /// Returns the job for a programme (reused when it exists) and starts downloading at
    /// <paramref name="offsetSeconds"/> so the first segments are ready when the player asks.
    /// </summary>
    public async Task<CatchupJob> OpenAsync(Guid backendUuid, DateTime startUtc, DateTime endUtc, Guid userId, double offsetSeconds, CancellationToken ct)
    {
        Cleanup();
        var job = _jobs.Values.FirstOrDefault(j => j.BackendUuid == backendUuid && j.StartUtc == startUtc);
        if (job is null)
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var dir = Path.Combine(Root, id);
            System.IO.Directory.CreateDirectory(dir);
            job = _jobs.GetOrAdd(id, _ => new CatchupJob(id, backendUuid, startUtc, endUtc, userId, dir));
            _logger.LogInformation("[Catch-up] Job {Job}: {Uuid} {Start:u}–{End:u}", job.Id, backendUuid, startUtc, endUtc);
        }

        var previousOwner = job.OwnerUserId;
        job.OwnerUserId = userId;
        job.LastAccessUtc = DateTime.UtcNow;
        var first = Math.Clamp((int)(offsetSeconds / SegmentSeconds), 0, job.TotalSegments - 1);
        try
        {
            await EnsureProducingAsync(job, first, ct).ConfigureAwait(false);
        }
        catch (CatchupBusyException)
        {
            job.OwnerUserId = previousOwner;
            throw;
        }

        return job;
    }

    /// <summary>Segments currently offered in the playlist (time-limited for a programme that is still airing).</summary>
    public static int VisibleSegments(CatchupJob job)
    {
        var elapsed = DateTime.UtcNow - LiveDelay - job.StartUtc;
        var byTime = (int)Math.Floor(elapsed.TotalSeconds / SegmentSeconds);
        return Math.Clamp(byTime, 0, job.TotalSegments);
    }

    /// <summary>HLS playlist. Finished programmes are VOD; airing ones are an EVENT playlist that grows.</summary>
    public static string BuildPlaylist(CatchupJob job, double? startOffsetSeconds)
    {
        var visible = VisibleSegments(job);
        var complete = visible >= job.TotalSegments;
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(SegmentSeconds + 4).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:").Append(complete ? "VOD" : "EVENT").Append('\n');
        double? offset = startOffsetSeconds ?? (complete ? null : 0d);
        if (offset is { } o)
        {
            sb.Append("#EXT-X-START:TIME-OFFSET=").Append(Math.Max(0, o).ToString("0.###", CultureInfo.InvariantCulture)).Append(",PRECISE=YES\n");
        }

        for (var i = 0; i < visible; i++)
        {
            sb.Append("#EXTINF:").Append(job.SegmentDuration(i).ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(CatchupJob.SegmentName(i)).Append('\n');
        }

        if (complete)
        {
            sb.Append("#EXT-X-ENDLIST\n");
        }

        return sb.ToString();
    }

    /// <summary>Path of a finished segment, producing it on demand. Null when it cannot be delivered.</summary>
    public async Task<string?> GetSegmentAsync(CatchupJob job, int index, CancellationToken ct)
    {
        if (index < 0 || index >= job.TotalSegments)
        {
            return null;
        }

        job.LastAccessUtc = DateTime.UtcNow;
        var path = job.SegmentPath(index);
        if (File.Exists(path))
        {
            return path;
        }

        // normally 25 s; a slot still on air is written in real time, so wait until shortly after it ends (max 40 s)
        var now = DateTime.UtcNow;
        var due = job.SegmentStartUtc(index + 1) + TimeSpan.FromSeconds(10);
        var until = now + TimeSpan.FromSeconds(25);
        if (due > until)
        {
            until = due < now + TimeSpan.FromSeconds(40) ? due : now + TimeSpan.FromSeconds(40);
        }

        await EnsureProducingAsync(job, index, ct).ConfigureAwait(false);
        var retried = false;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path))
            {
                return path;
            }

            if (!job.Downloading)
            {
                if (retried)
                {
                    break;
                }

                // chunk ended early (provider hiccup) – one fresh attempt from here
                retried = true;
                await EnsureProducingAsync(job, index, ct).ConfigureAwait(false);
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return File.Exists(path) ? path : null;
    }

    /// <summary>Makes sure a download is running that will soon write segment <paramref name="index"/>.</summary>
    private async Task EnsureProducingAsync(CatchupJob job, int index, CancellationToken ct)
    {
        await job.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(job.SegmentPath(index)))
            {
                return;
            }

            if (job.Downloading && index >= job.ChunkStart && index <= HighestWritten(job) + ReachSegments)
            {
                return; // the writer is on its way
            }

            // restart at a whole minute at or before the wanted slot
            var start = index / RestartAlignSegments * RestartAlignSegments;
            await StartChunkAsync(job, start, ct).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private static int HighestWritten(CatchupJob job)
    {
        var i = Math.Max(job.ChunkStart, 0);
        while (i < job.TotalSegments && File.Exists(job.SegmentPath(i)))
        {
            i++;
        }

        return i - 1;
    }

    private async Task StartChunkAsync(CatchupJob job, int startSegment, CancellationToken ct)
    {
        await _connection.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // one provider connection: never take it from another viewer who is actively watching
            foreach (var other in _jobs.Values.Where(j => j.Downloading && j != job))
            {
                if (other.OwnerUserId != job.OwnerUserId && DateTime.UtcNow - other.LastAccessUtc < OwnerIdle)
                {
                    throw new CatchupBusyException("The catch-up connection is in use by another viewer.");
                }
            }

            foreach (var other in _jobs.Values.Where(j => j.Downloading))
            {
                Kill(other);
            }

            var offset = TimeSpan.FromSeconds(startSegment * SegmentSeconds);
            var remaining = (int)Math.Ceiling(job.TotalSeconds - (startSegment * SegmentSeconds));
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining / 60.0));
            BackendSession session;
            try
            {
                session = await _backend.CreateSessionAsync(job.BackendUuid, job.StartUtc + offset, minutes, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // the provider may still count the connection we just closed
                _logger.LogDebug(ex, "[Catch-up] Session refused, retrying once");
                await Task.Delay(1500, ct).ConfigureAwait(false);
                session = await _backend.CreateSessionAsync(job.BackendUuid, job.StartUtc + offset, minutes, ct).ConfigureAwait(false);
            }

            StartFfmpeg(job, session.PlaybackUrl, startSegment, remaining);
            _logger.LogInformation("[Catch-up] Job {Job}: downloading from segment {Seg} ({Min} min)", job.Id, startSegment, minutes);
        }
        finally
        {
            _connection.Release();
        }
    }

    private void StartFfmpeg(CatchupJob job, string inputUrl, int startSegment, int seconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = job.Directory,
        };
        foreach (var a in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error",
                     "-fflags", "+genpts+discardcorrupt",
                     "-i", inputUrl,
                     "-map", "0:v:0?", "-map", "0:a?",
                     "-c", "copy",
                     "-t", seconds.ToString(CultureInfo.InvariantCulture),
                     "-output_ts_offset", (startSegment * SegmentSeconds).ToString(CultureInfo.InvariantCulture),
                     "-f", "hls",
                     "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
                     "-hls_list_size", "0",
                     "-start_number", startSegment.ToString(CultureInfo.InvariantCulture),
                     "-hls_flags", "temp_file",
                     "-hls_segment_filename", Path.Combine(job.Directory, "seg_%05d.ts"),
                     Path.Combine(job.Directory, "ffmpeg.m3u8"),
                 })
        {
            psi.ArgumentList.Add(a);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderr.Length < 4000)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            if (stderr.Length > 0)
            {
                job.LastError = stderr.ToString();
                _logger.LogInformation("[Catch-up] Job {Job} ffmpeg: {Err}", job.Id, Truncate(job.LastError, 500));
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        job.ChunkStart = startSegment;
        job.Process = process;
    }

    private void Kill(CatchupJob job)
    {
        try
        {
            if (job.Process is { HasExited: false } p)
            {
                p.Kill(true);
                p.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "[Catch-up] Stop failed");
        }

        // finished segments stay valid and are reused later
    }

    /// <summary>Removes jobs older than the TTL and keeps the cache under its size limit (least recently used first).</summary>
    private void Cleanup()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Root);
            var ignore = Path.Combine(Root, ".ignore");
            if (!File.Exists(ignore))
            {
                File.WriteAllText(ignore, string.Empty); // keep Jellyfin's library scanner out
            }

            var now = DateTime.UtcNow;
            foreach (var job in _jobs.Values.Where(j => !j.Downloading && now - j.LastAccessUtc > JobTtl).ToList())
            {
                _jobs.TryRemove(job.Id, out _);
                TryDeleteDir(job.Directory);
            }

            foreach (var dir in System.IO.Directory.EnumerateDirectories(Root))
            {
                if (!_jobs.ContainsKey(Path.GetFileName(dir)) && now - System.IO.Directory.GetLastWriteTimeUtc(dir) > JobTtl)
                {
                    TryDeleteDir(dir);
                }
            }

            var limit = Math.Max(1, Config.CacheLimitGb) * 1024L * 1024 * 1024;
            var dirs = System.IO.Directory.EnumerateDirectories(Root)
                .Select(d => (Dir: d, Size: DirSize(d), Time: LastUse(d)))
                .OrderBy(x => x.Time)
                .ToList();
            var total = dirs.Sum(x => x.Size);
            foreach (var (dir, size, _) in dirs)
            {
                if (total <= limit)
                {
                    break;
                }

                var id = Path.GetFileName(dir);
                if (_jobs.TryGetValue(id, out var job) && (job.Downloading || now - job.LastAccessUtc < OwnerIdle))
                {
                    continue;
                }

                _jobs.TryRemove(id, out _);
                TryDeleteDir(dir);
                total -= size;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Catch-up] Cache cleanup failed in {Root}", Root);
        }
    }

    private DateTime LastUse(string dir) =>
        _jobs.TryGetValue(Path.GetFileName(dir), out var job) ? job.LastAccessUtc : System.IO.Directory.GetLastWriteTimeUtc(dir);

    private static long DirSize(string dir)
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(dir).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            // only ever inside the plugin's own cache root
            var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(dir).StartsWith(root, StringComparison.Ordinal))
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[Catch-up] Could not delete {Dir}", dir);
        }
    }

    private static string Truncate(string? s, int n) => s is null ? string.Empty : (s.Length <= n ? s : s[..n]);

    public void Dispose()
    {
        foreach (var job in _jobs.Values)
        {
            Kill(job);
        }

        _connection.Dispose();
    }
}
