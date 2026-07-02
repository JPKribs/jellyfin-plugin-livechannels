using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Measures how many concurrent channel streams the server can sustain, so "Maximum concurrent streams" can be
/// set from evidence instead of guesswork. Each round runs N copies of the EXACT production producer command
/// (same decode decisions, same encoder, same realtime pacing) over a fixed slice of the chosen item, with each
/// copy seeked to a different section. A stream that keeps up finishes its slice at ~30 fps (the channel output
/// rate); one that cannot comes in under. Rounds add one stream at a time until any stream drops below the bar,
/// and the recommendation is the last fully passing round.
/// </summary>
public sealed class StressTestService : IDisposable
{
    // How much content each stream encodes per round. Long enough to get past encoder warm-up and represent a
    // steady state; with production pacing a passing round takes about this long in wall time.
    private static readonly TimeSpan SliceLength = TimeSpan.FromSeconds(60);

    // The pass bar. Channel output is a constant 30 fps, so 30 is exactly realtime; the small allowance absorbs
    // process start-up inside the measurement window.
    private const double PassFps = 29.0;

    // Output frames per slice: SliceLength at the channel's constant 30 fps.
    private const double SliceFrames = 60.0 * 30.0;

    // Hard ceiling on rounds; a home server sustaining more concurrent 60s encodes than this does not need a test.
    private const int MaxStreams = 8;

    // A round that runs this long has streams far below realtime; kill it and call the round failed.
    private static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(4);

    private readonly IMediaEncoder _encoder;
    private readonly StreamSessionService _streams;
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILogger<StressTestService> _logger;

    private readonly object _lock = new();
    private readonly List<StressRound> _rounds = new();
    private bool _running;
    private int _currentStreams;
    private int? _recommended;
    private string? _error;
    private string? _itemName;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="StressTestService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="streams">The stream session service, whose production argument builder the test reuses.</param>
    /// <param name="library">The library manager, used to resolve the chosen item.</param>
    /// <param name="mediaSources">The media source manager, used to read the item's video stream properties.</param>
    /// <param name="logger">The logger.</param>
    public StressTestService(IMediaEncoder encoder, StreamSessionService streams, ILibraryManager library, IMediaSourceManager mediaSources, ILogger<StressTestService> logger)
    {
        _encoder = encoder;
        _streams = streams;
        _library = library;
        _mediaSources = mediaSources;
        _logger = logger;
    }

    /// <summary>Gets a snapshot of the test for the settings page.</summary>
    /// <returns>The current status.</returns>
    public StressStatus GetStatus()
    {
        lock (_lock)
        {
            return new StressStatus(_running, _currentStreams, _rounds.ToList(), _recommended, _error, _itemName);
        }
    }

    /// <summary>
    /// Starts a stress test against the given library item. Returns an error message when it cannot start
    /// (already running, unknown item, unreadable file), or <c>null</c> when the test is off and running.
    /// </summary>
    /// <param name="itemId">The library item to encode.</param>
    /// <returns>An error message, or <c>null</c> on success.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "The admin-supplied id only SELECTS a library item; the path checked and encoded is the item's own path from Jellyfin's library database, never request-derived text.")]
    public string? TryStart(Guid itemId)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return "No ffmpeg encoder is configured.";
        }

        var item = _library.GetItemById(itemId);
        var path = item?.Path;
        if (item is null || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return "That item's media file could not be found.";
        }

        MediaStream? video;
        try
        {
            video = _mediaSources.GetMediaStreams(itemId).FirstOrDefault(s => s.Type == MediaStreamType.Video);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stress test could not read media streams for {ItemId}", itemId);
            video = null;
        }

        var height = video?.Height ?? 0;
        var isHdr = ChannelService.ComputeIsHdr(video);
        var runtime = item.RunTimeTicks is long ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;

        CancellationToken token;
        lock (_lock)
        {
            if (_running)
            {
                return "A stress test is already running.";
            }

            _running = true;
            _currentStreams = 0;
            _rounds.Clear();
            _recommended = null;
            _error = null;
            _itemName = item.Name;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _logger.LogInformation("Live Channels: stress test starting against \"{Name}\" ({Height}p, HDR {Hdr})", item.Name, height, isHdr);
        _ = Task.Run(() => RunAsync(ffmpeg, path, height, isHdr, runtime, token), CancellationToken.None);
        return null;
    }

    /// <summary>Cancels a running test; its processes are killed and the partial rounds remain visible.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }

    /// <summary>
    /// The recommendation from a set of completed rounds: the highest stream count whose round fully passed
    /// (rounds run 1, 2, 3, … and stop at the first failure). Zero when even one stream could not keep up.
    /// </summary>
    /// <param name="rounds">The completed rounds.</param>
    /// <returns>The recommended maximum concurrent streams.</returns>
    public static int Recommend(IEnumerable<StressRound> rounds)
        => rounds.Where(r => r.Passed).Select(r => r.Streams).DefaultIfEmpty(0).Max();

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task RunAsync(string ffmpeg, string path, int height, bool isHdr, TimeSpan runtime, CancellationToken token)
    {
        try
        {
            for (var streams = 1; streams <= MaxStreams && !token.IsCancellationRequested; streams++)
            {
                lock (_lock)
                {
                    _currentStreams = streams;
                }

                var minFps = await RunRoundAsync(ffmpeg, path, height, isHdr, runtime, streams, token).ConfigureAwait(false);
                var round = new StressRound(streams, Math.Round(minFps, 1), minFps >= PassFps);
                _logger.LogInformation("Live Channels: stress round {Streams} stream(s) -> slowest {Fps:F1} fps ({Verdict})", streams, minFps, round.Passed ? "pass" : "fail");

                lock (_lock)
                {
                    _rounds.Add(round);
                }

                if (!round.Passed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the settings page; keep the rounds finished so far.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Channels: stress test failed");
            lock (_lock)
            {
                _error = "The test failed: " + ex.Message;
            }
        }
        finally
        {
            lock (_lock)
            {
                _recommended = Recommend(_rounds);
                _currentStreams = 0;
                _running = false;
            }
        }
    }

    // Runs one round: N concurrent copies of the production command, each seeked to a different section, and
    // returns the slowest stream's output frame rate.
    private async Task<double> RunRoundAsync(string ffmpeg, string path, int height, bool isHdr, TimeSpan runtime, int streams, CancellationToken token)
    {
        var tasks = new List<Task<double>>(streams);
        for (var i = 0; i < streams; i++)
        {
            var offset = OffsetFor(i, runtime);
            var (args, _) = _streams.BuildStressArguments(path, height, isHdr, offset, SliceLength);
            tasks.Add(RunProducerAsync(ffmpeg, args, token));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Min();
    }

    // Spreads concurrent copies across the file so they exercise different sections, clamped so every copy has a
    // full slice to read even in short items.
    private static TimeSpan OffsetFor(int index, TimeSpan runtime)
    {
        var desired = TimeSpan.FromSeconds(300 + (index * 120));
        var usable = runtime - SliceLength - TimeSpan.FromSeconds(30);
        if (usable <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return desired < usable ? desired : TimeSpan.FromTicks(usable.Ticks * (index + 1) / (index + 2));
    }

    // Runs one producer to completion, discarding its output, and returns its output frame rate measured from
    // the first byte it produces (excluding process start-up) to exit. Zero when it produced nothing.
    private async Task<double> RunProducerAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RoundTimeout);

        // Drain stderr so ffmpeg can never stall on a full pipe; the content is not needed.
        var stderr = DrainAsync(process.StandardError.BaseStream, timeout.Token);

        long firstByteStamp = 0;
        var buffer = new byte[81920];
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (firstByteStamp == 0)
                {
                    firstByteStamp = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled: kill and fall through; a killed stream scores by what it managed.
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await stderr.ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        if (firstByteStamp == 0)
        {
            return 0;
        }

        var wallSeconds = (Stopwatch.GetTimestamp() - firstByteStamp) / (double)Stopwatch.Frequency;
        return wallSeconds > 0 ? SliceFrames / wallSeconds : 0;
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // Round over; nothing to drain.
        }
        catch (IOException)
        {
            // Pipe closed with the process; done.
        }
    }
}
