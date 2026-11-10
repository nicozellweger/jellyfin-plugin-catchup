using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>
/// Downloads one catch-up programme sequentially into a cache file and lets any
/// number of ranged readers follow it. Players probe and seek a lot; without this
/// every seek would open a new upstream (provider) connection, which providers with
/// a connection limit answer slowly or not at all.
/// </summary>
public sealed class CatchupBuffer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private long _written;
    private volatile bool _completed;
    private volatile bool _failed;

    public CatchupBuffer(string path, ILogger logger)
    {
        FilePath = path;
        _logger = logger;
        CreatedUtc = DateTime.UtcNow;
    }

    public string FilePath { get; }

    public DateTime CreatedUtc { get; }

    /// <summary>Total size announced by the upstream, null when unknown.</summary>
    public long? TotalLength { get; private set; }

    public long Written => Interlocked.Read(ref _written);

    public bool Completed => _completed;

    public bool Failed => _failed;

    /// <summary>Starts the download. Completes once the upstream headers arrived.</summary>
    public async Task StartAsync(Func<CancellationToken, Task<HttpResponseMessage>> open)
    {
        var response = await open(_cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _failed = true;
            response.Dispose();
            throw new HttpRequestException($"Upstream answered {(int)response.StatusCode}");
        }

        TotalLength = response.Content.Headers.ContentLength;
        _ = Task.Run(() => CopyAsync(response));
    }

    private async Task CopyAsync(HttpResponseMessage response)
    {
        try
        {
            using (response)
            await using (var src = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
            await using (var dst = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                    await dst.FlushAsync(_cts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _written, read);
                }
            }

            _completed = true;
            TotalLength ??= Written;
            _logger.LogInformation("[Catch-up] Buffered {Bytes:N0} bytes into {File}", Written, Path.GetFileName(FilePath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failed = true;
            _logger.LogWarning(ex, "[Catch-up] Buffering failed after {Bytes:N0} bytes", Written);
        }
        catch (OperationCanceledException)
        {
            _failed = true;
        }
    }

    /// <summary>
    /// Copies [start, end] to <paramref name="output"/>, waiting for data that has not
    /// been downloaded yet. Returns when done, the client left, or the download died.
    /// </summary>
    public async Task CopyRangeAsync(Stream output, long start, long? endInclusive, CancellationToken ct)
    {
        var position = start;
        var buffer = new byte[1 << 16];
        var idleSince = DateTime.UtcNow;
        await using var file = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
        while (!ct.IsCancellationRequested)
        {
            var limit = endInclusive.HasValue ? endInclusive.Value + 1 : long.MaxValue;
            if (position >= limit)
            {
                return;
            }

            var available = Written;
            if (position < available)
            {
                file.Position = position;
                var want = (int)Math.Min(buffer.Length, Math.Min(available, limit) - position);
                var read = await file.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    position += read;
                    idleSince = DateTime.UtcNow;
                    continue;
                }
            }

            if (Completed || Failed)
            {
                return;
            }

            if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(60))
            {
                return; // upstream stalled
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
