using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

    // A stream must deliver nearly its whole slice to count: one that hit the end of the file early or died
    // part-way finished "fast" only because it had less to do.
    private const double MinCompleteFraction = 0.95;

    // How long a producer that closed its output gets to exit on its own before it is killed.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

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
        if (runtime < SliceLength + TimeSpan.FromSeconds(30))
        {
            return "Pick an item longer than 90 seconds (this one is too short, or its length is unknown).";
        }

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
    /// Cancels the test when one is running because a real viewer needs the encoder. The start path already
    /// refuses to run while channels are streaming; this covers the reverse order, where a viewer tunes in
    /// mid-test, which would both stutter their stream and skew the measurement.
    /// </summary>
    /// <returns>Whether a running test was cancelled.</returns>
    public bool CancelForViewer()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return false;
            }

            _error = "Cancelled: a viewer tuned in to a channel, so the encoder is no longer idle.";
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }

            return true;
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
        lock (_lock)
        {
            // Cancel first so any running round kills its ffmpeg processes instead of leaving them to finish.
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            _cts?.Dispose();
            _cts = null;
        }
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

                var (minFps, allCompleted) = await RunRoundAsync(ffmpeg, path, height, isHdr, runtime, streams, token).ConfigureAwait(false);
                var round = new StressRound(streams, Math.Round(minFps, 1), allCompleted && minFps >= PassFps);
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
    // returns the slowest stream's output frame rate and whether every stream exited cleanly with a full slice.
    private async Task<(double MinFps, bool AllCompleted)> RunRoundAsync(string ffmpeg, string path, int height, bool isHdr, TimeSpan runtime, int streams, CancellationToken token)
    {
        var tasks = new List<Task<ProducerResult>>(streams);
        for (var i = 0; i < streams; i++)
        {
            var offset = OffsetFor(i, runtime);
            var (args, _) = _streams.BuildStressArguments(path, height, isHdr, offset, SliceLength);
            tasks.Add(RunProducerAsync(ffmpeg, args, token));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return (results.Min(r => r.Fps), results.All(r => r.Completed));
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

    // Runs one producer to completion, discarding its output. Its frame rate is the frames ffmpeg reports encoding
    // (from -progress on stderr) over the time from its first output byte (excluding process start-up) to exit.
    // It completed only if it exited cleanly having encoded nearly the whole slice.
    private async Task<ProducerResult> RunProducerAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken token)
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

        // Read stderr throughout (so ffmpeg can never stall on a full pipe), keeping the last reported frame count.
        var progress = ReadFramesAsync(process.StandardError, timeout.Token);

        long firstByteStamp = 0;
        var buffer = new byte[81920];
        try
        {
            // Only the first byte's arrival time matters; the data itself is discarded.
            var stdout = process.StandardOutput.BaseStream;
            while (await stdout.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) > 0)
            {
                if (firstByteStamp == 0)
                {
                    firstByteStamp = Stopwatch.GetTimestamp();
                }
            }

            // Output closed: ffmpeg is finishing up. Let it exit on its own so its exit code is real.
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            grace.CancelAfter(ExitGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
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
        }

        var endStamp = Stopwatch.GetTimestamp();
        var frames = await progress.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using var reaped = new CancellationTokenSource(ExitGrace);
        try
        {
            await process.WaitForExitAsync(reaped.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Could not be reaped in time; scored as not completed below.
        }

        var cleanExit = process.HasExited && process.ExitCode == 0;
        if (firstByteStamp == 0)
        {
            return new ProducerResult(0, false);
        }

        var wallSeconds = (endStamp - firstByteStamp) / (double)Stopwatch.Frequency;
        var fps = wallSeconds > 0 ? frames / wallSeconds : 0;
        return new ProducerResult(fps, cleanExit && frames >= SliceFrames * MinCompleteFraction);
    }

    /// <summary>
    /// Parses a frame count from one line of ffmpeg's <c>-progress</c> output (<c>frame=1234</c>).
    /// </summary>
    /// <param name="line">The progress line.</param>
    /// <param name="frames">The frame count when the line carries one.</param>
    /// <returns>Whether the line was a frame count.</returns>
    internal static bool TryParseFrames(string? line, out long frames)
    {
        frames = 0;
        const string Prefix = "frame=";
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return long.TryParse(line.AsSpan(Prefix.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out frames);
    }

    private static async Task<long> ReadFramesAsync(StreamReader reader, CancellationToken token)
    {
        long frames = 0;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (TryParseFrames(line, out var value))
                {
                    frames = value;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Round over; keep the last count seen.
        }
        catch (IOException)
        {
            // Pipe closed with the process; done.
        }

        return frames;
    }

    private readonly record struct ProducerResult(double Fps, bool Completed);
}
