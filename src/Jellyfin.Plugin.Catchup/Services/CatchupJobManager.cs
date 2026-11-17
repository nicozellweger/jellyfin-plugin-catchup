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

    /// <summary>When a viewer last opened this programme. The most recently opened one owns the connection.</summary>
    internal DateTime OpenedUtc { get; set; }

    /// <summary>When the running chunk was started (for the download speed).</summary>
    internal DateTime ChunkStartedUtc { get; set; }

    internal string? LastError { get; set; }

    /// <summary>The running chunk was stopped by us (seek, other programme), not by end of stream.</summary>
    internal bool StoppedOnPurpose { get; set; }

    internal int StartFailures { get; set; }

    /// <summary>Last slot a player asked for (≈ play position).</summary>
    internal int LastRequestedSegment { get; set; }

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
    // A slot the writer reaches within this time is waited for; anything further restarts the download
    // at the slot (a restart costs about 5 s). Measured, because providers deliver anywhere between
    // 8x and 25x real time. Jellyfin's remuxer reads just ahead of the writer and must not trigger restarts.
    private static readonly TimeSpan ReachTime = TimeSpan.FromSeconds(8);
    private const int ReachSegmentsMin = 12;
    private static readonly TimeSpan JobTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan OwnerIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LiveDelay = TimeSpan.FromSeconds(20);
    // nobody asked for this long: the viewer is gone, stop downloading and free the provider connection
    private static readonly TimeSpan DownloadIdle = TimeSpan.FromSeconds(90);

    private readonly ICatchupBackend _backend;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<CatchupJobManager> _logger;
    private readonly ConcurrentDictionary<string, CatchupJob> _jobs = new();
    private readonly SemaphoreSlim _connection = new(1, 1);

    public CatchupJobManager(ICatchupBackend backend, IMediaEncoder mediaEncoder, ILogger<CatchupJobManager> logger)
    {
        _backend = backend;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _watchdog = new Timer(_ => StopIdleDownloads(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private readonly Timer _watchdog;

    /// <summary>
    /// A download outlives its viewer otherwise: it would load the rest of the programme (gigabytes
    /// for a match) and hold the provider's only connection. What is on disk stays and is reused.
    /// </summary>
    private void StopIdleDownloads()
    {
        try
        {
            foreach (var job in _jobs.Values.Where(j => j.Downloading && DateTime.UtcNow - j.LastAccessUtc > DownloadIdle))
            {
                _logger.LogInformation("[Catch-up] Job {Job}: no viewer for {Sec} s, stopping download", job.Id, (int)DownloadIdle.TotalSeconds);
                Kill(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Catch-up] Idle check failed");
        }
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
    /// <remarks>Without an offset a download that is already running for the job is left alone
    /// (the player was sent somewhere else on purpose); otherwise it starts at the beginning.</remarks>
    public async Task<CatchupJob> OpenAsync(Guid backendUuid, DateTime startUtc, DateTime endUtc, Guid userId, double? offsetSeconds, CancellationToken ct)
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

        job.OpenedUtc = DateTime.UtcNow;
        var previousOwner = job.OwnerUserId;
        if (userId != Guid.Empty || previousOwner == Guid.Empty)
        {
            job.OwnerUserId = userId; // a call without a user (server-internal) never takes a job over
        }

        job.LastAccessUtc = DateTime.UtcNow;
        if (offsetSeconds is null && job.Downloading)
        {
            return job;
        }

        var first = Math.Clamp((int)((offsetSeconds ?? 0) / SegmentSeconds), 0, job.TotalSegments - 1);
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

    /// <summary>
    /// HLS playlist covering the whole programme (VOD), also while it is still on air: slots in the
    /// future are delivered in real time (the request waits), so the timeline always shows the full
    /// programme and seeking works with every player and with Jellyfin's own remuxer.
    /// </summary>
    public static string BuildPlaylist(CatchupJob job, double? startOffsetSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-TARGETDURATION:").Append(SegmentSeconds + 4).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        if (startOffsetSeconds is { } o && o > 0)
        {
            sb.Append("#EXT-X-START:TIME-OFFSET=").Append(o.ToString("0.###", CultureInfo.InvariantCulture)).Append(",PRECISE=YES\n");
        }

        for (var i = 0; i < job.TotalSegments; i++)
        {
            sb.Append("#EXTINF:").Append(job.SegmentDuration(i).ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(CatchupJob.SegmentName(i)).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>Any finished segment of the job, for reading the stream layout: one that is on disk,
    /// else the first one the running download writes, else the start of the programme.</summary>
    public async Task<string?> GetProbeSegmentAsync(CatchupJob job, CancellationToken ct)
    {
        try
        {
            var existing = System.IO.Directory.EnumerateFiles(job.Directory, "seg_*.ts").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (existing is not null)
            {
                return existing;
            }
        }
        catch (IOException)
        {
        }

        return await GetSegmentAsync(job, job.Downloading ? Math.Max(job.ChunkStart, 0) : 0, ct).ConfigureAwait(false);
    }

    /// <summary>Path of a finished segment, producing it on demand. Null when it cannot be delivered.</summary>
    public async Task<string?> GetSegmentAsync(CatchupJob job, int index, CancellationToken ct)
    {
        if (index < 0 || index >= job.TotalSegments)
        {
            return null;
        }

        job.LastAccessUtc = DateTime.UtcNow;
        job.LastRequestedSegment = index;
        var path = job.SegmentPath(index);
        if (File.Exists(path))
        {
            return path;
        }

        // normally 30 s; a slot still on air is written in real time, so wait until shortly after it has aired.
        // Slots far in the future (a seek past "now") are refused at once.
        var now = DateTime.UtcNow;
        var due = job.SegmentStartUtc(index + 1) + TimeSpan.FromSeconds(15);
        if (due - now > TimeSpan.FromMinutes(3))
        {
            return null;
        }

        var until = due > now + TimeSpan.FromSeconds(30) ? due : now + TimeSpan.FromSeconds(30);

        await EnsureProducingAsync(job, index, ct).ConfigureAwait(false);
        var idleSince = DateTime.MaxValue;
        var checkedAt = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path))
            {
                return path;
            }

            if (job.Downloading)
            {
                idleSince = DateTime.MaxValue;
                // The download may have been sent elsewhere meanwhile (an older reader, a gap fill).
                // While this is still the newest request, bring it back instead of waiting in vain.
                if (job.LastRequestedSegment == index && DateTime.UtcNow - checkedAt > TimeSpan.FromSeconds(3))
                {
                    checkedAt = DateTime.UtcNow;
                    await EnsureProducingAsync(job, index, ct).ConfigureAwait(false);
                }
            }
            else if (idleSince == DateTime.MaxValue)
            {
                idleSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(10))
            {
                // nothing running and no automatic retry came: start again from here
                idleSince = DateTime.MaxValue;
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

            if (job.Downloading && index >= job.ChunkStart)
            {
                var highest = HighestWritten(job);
                var elapsed = (DateTime.UtcNow - job.ChunkStartedUtc).TotalSeconds;
                var written = highest - job.ChunkStart + 1;
                // slots per second; until there is something to measure assume a slow provider
                var rate = elapsed > 3 && written > 3 ? written / elapsed : 1.2;
                var reach = Math.Max(ReachSegmentsMin, (int)(rate * ReachTime.TotalSeconds));
                if (index <= highest + reach)
                {
                    return; // the writer is on its way
                }
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

            // One connection, several readers: a programme opened earlier may still be read by a stream
            // nobody watches any more (a transcode that was not closed). It must not take the connection
            // back from what the viewer opened last, or the two would interrupt each other forever.
            foreach (var other in _jobs.Values.Where(j => j.Downloading && j != job))
            {
                if (other.OpenedUtc > job.OpenedUtc && DateTime.UtcNow - other.LastAccessUtc < TimeSpan.FromSeconds(30))
                {
                    throw new CatchupBusyException("A programme opened later is using the catch-up connection.");
                }
            }

            var killed = false;
            foreach (var other in _jobs.Values.Where(j => j.Downloading))
            {
                Kill(other);
                killed = true;
            }

            if (killed)
            {
                // give the provider a moment to release the connection we just closed
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }

            // download only up to the next part that is already on disk (plus one slot of overlap)
            var stop = job.TotalSegments;
            for (var i = startSegment + RestartAlignSegments; i < job.TotalSegments; i++)
            {
                if (File.Exists(job.SegmentPath(i)))
                {
                    stop = Math.Min(job.TotalSegments, i + 1);
                    break;
                }
            }

            var offset = TimeSpan.FromSeconds(startSegment * SegmentSeconds);
            var remaining = stop >= job.TotalSegments
                ? (int)Math.Ceiling(job.TotalSeconds - (startSegment * SegmentSeconds))
                : (stop - startSegment) * SegmentSeconds;
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
        // The URL comes from the backend's answer. ffmpeg would also read file:, concat: and friends,
        // so a backend that answers with something else than a web address must not reach it.
        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out var input) || (input.Scheme != Uri.UriSchemeHttp && input.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException("The catch-up backend returned an address that is not http(s).");
        }

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
                     "-protocol_whitelist", "http,https,tcp,tls,crypto",
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
            job.LastError = stderr.Length > 0 ? Redact(stderr.ToString()) : null;
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }

            _logger.LogInformation(
                "[Catch-up] Job {Job}: chunk from {Start} ended (code {Code}, {Purpose}, written up to {Last}) {Err}",
                job.Id,
                startSegment,
                code,
                job.StoppedOnPurpose ? "stopped" : "natural",
                HighestWritten(job),
                Truncate(job.LastError, 300));

            if (!ReferenceEquals(job.Process, process) || job.StoppedOnPurpose || DateTime.UtcNow - job.LastAccessUtc > OwnerIdle)
            {
                return;
            }

            // the provider refused (it may still count the connection we just closed): retry the same chunk
            if (code != 0 && HighestWritten(job) < startSegment)
            {
                var attempt = ++job.StartFailures;
                if (attempt <= 4)
                {
                    _logger.LogInformation("[Catch-up] Job {Job}: retrying chunk {Start} (attempt {Attempt})", job.Id, startSegment, attempt);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
                            await job.Gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (ReferenceEquals(job.Process, process))
                                {
                                    await StartChunkAsync(job, startSegment, CancellationToken.None).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                job.Gate.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[Catch-up] Retry for {Job} not started", job.Id);
                        }
                    });
                }

                return;
            }

            job.StartFailures = 0;
            if (code != 0)
            {
                return; // broke off mid-stream; the next segment request restarts it where needed
            }

            // fill missing parts while the viewer is still around: first ahead of the play position, then the rest
            var from = Math.Clamp(job.LastRequestedSegment, 0, job.TotalSegments - 1);
            var gap = Enumerable.Range(from, job.TotalSegments - from)
                .Concat(Enumerable.Range(0, from))
                .FirstOrDefault(i => !File.Exists(job.SegmentPath(i)), -1);
            if (gap >= 0 && job.SegmentStartUtc(gap + 1) < DateTime.UtcNow)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EnsureProducingAsync(job, gap, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Catch-up] Gap fill for {Job} not started", job.Id);
                    }
                });
            }
        };
        job.StoppedOnPurpose = false;
        job.ChunkStartedUtc = DateTime.UtcNow;
        job.ChunkStart = startSegment;
        job.Process = process;
        process.Start();
        process.BeginErrorReadLine();
    }

    private void Kill(CatchupJob job)
    {
        try
        {
            if (job.Process is { HasExited: false } p)
            {
                job.StoppedOnPurpose = true;
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

    /// <summary>ffmpeg names its input in error messages; stream URLs can carry account credentials.</summary>
    private static string Redact(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "(/timeshift/)[^/\\s]+/[^/\\s]+/", "$1***/***/");
        return System.Text.RegularExpressions.Regex.Replace(s, "((?:username|password|token|api_key)=)[^&\\s]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string Truncate(string? s, int n) => s is null ? string.Empty : (s.Length <= n ? s : s[..n]);

    public void Dispose()
    {
        _watchdog.Dispose();
        foreach (var job in _jobs.Values)
        {
            Kill(job);
        }

        _connection.Dispose();
    }
}
