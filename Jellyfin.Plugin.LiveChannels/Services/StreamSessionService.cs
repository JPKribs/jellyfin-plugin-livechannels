using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Streams a channel as a continuous MPEG-TS feed. It computes which item is airing now from the same
/// wall-clock schedule the guide uses, seeks into it, and then plays each subsequent item end to end via
/// ffmpeg, looping until the client disconnects.
/// </summary>
public class StreamSessionService
{
    private const int BufferSize = 81920;

    // How many consecutive per-item failures (missing files, instant encoder deaths) before the channel gives up
    // and shows standby. Failures reset on any success, so this only trips when the library is genuinely
    // unreachable — and then quickly (~5s), instead of walking every item of a large playlist first.
    private const int MaxConsecutiveFailures = 25;

    // Where pre-extracted burn-in subtitles for tune-in items are written.
    private readonly string _subtitleRoot = Path.Combine(Path.GetTempPath(), "livechannels-subs");

    private readonly IMediaEncoder _encoder;
    private readonly ChannelService _channels;
    private readonly EncoderResolver _encoders;
    private readonly ILogger<StreamSessionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSessionService"/> class.
    /// </summary>
    /// <param name="encoder">The media encoder, used to locate ffmpeg.</param>
    /// <param name="channels">The channel service, used to resolve and schedule the channel's items.</param>
    /// <param name="encoders">The encoder resolver, used to pick software/hardware encoders.</param>
    /// <param name="logger">The logger.</param>
    public StreamSessionService(IMediaEncoder encoder, ChannelService channels, EncoderResolver encoders, ILogger<StreamSessionService> logger)
    {
        _encoder = encoder;
        _channels = channels;
        _encoders = encoders;
        _logger = logger;
    }

    /// <summary>
    /// Streams the channel to <paramref name="output"/> until cancellation. Each call is independent so the
    /// in-process Live TV service can run a separate encode per viewer; cancelling stops the encode.
    /// </summary>
    /// <param name="channel">The channel to stream.</param>
    /// <param name="output">The destination stream (the temp file Jellyfin reads).</param>
    /// <param name="cancellationToken">Cancelled when the live stream is closed.</param>
    /// <param name="stats">The shared per-session stats sink, or <c>null</c>.</param>
    /// <returns>A task that completes when streaming stops.</returns>
    public async Task StreamToAsync(Channel channel, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(output);

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("No ffmpeg encoder is configured; cannot stream channel {Name}", channel.Name);
            return;
        }

        var programs = _channels.ResolvePrograms(channel);
        if (programs.Count == 0)
        {
            _logger.LogWarning("Live channel {Name} resolved to no playable items; showing standby", channel.Name);
            await StreamSlateAsync(ffmpeg, TimeSpan.Zero, output, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (index, offset) = ScheduleCalculator.CurrentProgram(programs, DateTime.UtcNow, ScheduleCalculator.Epoch);

        // The seamless concat pipeline must software-decode (hardware decoders fail on the per-item resolution
        // changes a playlist produces), and software decoding can't keep up with >1080p sources (e.g. 4K HEVC).
        // So a channel containing any high-resolution item, or one using subtitle burn-in (which needs a
        // per-item filter graph), streams item by item; that path hardware-decodes each item at its own size.
        // HDR also forces the per-item path: only it tone-maps each item to SDR, so a channel with any HDR item
        // (even <=1080p) plays with correct colour instead of washed-out grey.
        // A GPU-upload encoder (QSV/VAAPI, whose pixel stage ends in hwupload) cannot survive the filter-graph
        // reinit the concat demuxer triggers when the playlist crosses a resolution/format change: the hwupload
        // fails to re-init ("Impossible to convert between formats", exit 218). The per-item path runs one ffmpeg
        // per item, so there is no mid-stream reinit. Force those encoders per-item.
        var videoCodec = Plugin.Instance?.ReadConfiguration(c => c.VideoCodec) ?? Models.VideoCodec.H264;
        var usesHwUpload = _encoders.ResolveVideo(videoCodec, allowHardware: true).PixelStage.Contains("hwupload", StringComparison.Ordinal);
        var highRes = programs.Any(p => p.SourceHeight > 1080);

        // Whether any item is HDR (which forces the per-item tone-map path). Each item's HDR flag is probed once at
        // guide refresh and cached on the schedule entry, so this is a pure in-memory scan with no media-stream
        // queries on the tune-in critical path. The short-circuit skips even the scan when the channel is
        // already per-item for another reason (burn-in, a >1080p item, or a GPU-upload encoder).
        var alreadyPerItem = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never || highRes || usesHwUpload;
        var hasHdr = !alreadyPerItem && programs.Any(p => p.IsHdr);
        var perItem = alreadyPerItem || hasHdr;
        var uniform = programs.Count > 0 && programs[0].SourceHeight > 0 && programs.All(p => p.SourceHeight == programs[0].SourceHeight);
        LogEncodePlan(channel, programs.Count, index, perItem, uniform, hasHdr);

        if (perItem)
        {
            await StreamPerItemLoopAsync(ffmpeg, channel, programs, index, offset, output, cancellationToken, stats).ConfigureAwait(false);
        }
        else
        {
            await StreamConcatAsync(ffmpeg, channel, programs, output, cancellationToken, stats).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams the channel as a self-trimming HLS playlist in <paramref name="hlsDir"/>. A long-lived segmenter
    /// ffmpeg copies the producer's continuous MPEG-TS into a rolling window of small segments plus a live
    /// playlist, so on-disk size stays bounded for any watch length. The producer (per-item or concat, plus the
    /// standby slate) feeds the segmenter's stdin through the existing <see cref="StreamToAsync"/> path.
    /// </summary>
    /// <param name="channel">The channel to stream.</param>
    /// <param name="hlsDir">The directory the playlist and segments are written to.</param>
    /// <param name="cancellationToken">Cancelled when the live stream is closed.</param>
    /// <returns>A task that completes when streaming stops.</returns>
    public async Task StreamToHlsAsync(Channel channel, string hlsDir, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(hlsDir);

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("No ffmpeg encoder is configured; cannot stream channel {Name}", channel.Name);
            return;
        }

        // The rolling window is fixed at 5 minutes. Jellyfin's reader stays at the live edge (it is not
        // realtime-paced), so a larger window buys nothing; a smaller one risks dropping the reader during
        // worst-case self-heal (the concat thrash guard can advance the edge up to 6 restarts x 30s of burst
        // before standing by). Disk cost is bounded at roughly bitrate x 5 minutes per active channel.
        var listSize = StreamArguments.SegmentsForWindow(5);
        var args = StreamArguments.BuildHlsSegmenter(Path.Combine(hlsDir, "seg%d.ts"), Path.Combine(hlsDir, "stream.m3u8"), listSize);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogInformation("Live Channels: HLS segmenter [{Name}]: {Ffmpeg} {Args}", channel.Name, ffmpeg, string.Join(' ', args));
        stats?.AppendLog("HLS segmenter\n$ ffmpeg " + string.Join(' ', args));

        using var segmenter = new Process { StartInfo = startInfo };
        try
        {
            segmenter.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the HLS segmenter for {Name}", channel.Name);
            return;
        }

        // The segmenter only copies; its speed is never the bottleneck, so it needs no stats sink.
        var stderrTask = ReadStandardErrorAsync(segmenter, null, cancellationToken);
        try
        {
            // The per-item/concat producer and the slate all write to this one stream, so the segmenter receives
            // a single unbroken TS feed it can package into the playlist.
            await StreamToAsync(channel, segmenter.StandardInput.BaseStream, cancellationToken, stats).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                segmenter.StandardInput.Close();
            }
            catch (Exception)
            {
                // The segmenter may already have exited; nothing to flush.
            }

            await KillAndWaitAsync(segmenter).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("Live Channels: HLS segmenter ({Name}): {Error}", channel.Name, stderr.Trim());
            }

            stats?.AppendLog("HLS segmenter: closed" + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "\n" + stderr.Trim()));
        }
    }

    // Streams the channel as ONE continuous ffmpeg using the concat demuxer, so item boundaries are seamless
    // (no timestamp or continuity reset). Software decode (hardware decoders fail on the per-segment resolution
    // changes a playlist produces) with the hardware encoder.
    private async Task StreamConcatAsync(string ffmpeg, Channel channel, IReadOnlyList<ProgramEntry> programs, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        var playable = programs.Where(p => !string.IsNullOrEmpty(p.Path) && File.Exists(p.Path)).ToList();
        if (playable.Count == 0)
        {
            _logger.LogWarning("Every item on channel {Name} is unreachable; showing standby", channel.Name);
            await StreamSlateAsync(ffmpeg, TimeSpan.Zero, output, cancellationToken).ConfigureAwait(false);
            return;
        }

        var listFile = Path.Combine(
            Path.GetTempPath(),
            "livechannels-" + channel.Number.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // The concat demuxer reads "file '<path>'" lines; single quotes in a path are escaped as '\''.
            await File.WriteAllLinesAsync(
                listFile,
                playable.Select(p => "file '" + p.Path!.Replace("'", "'\\''", StringComparison.Ordinal) + "'"),
                cancellationToken).ConfigureAwait(false);

            var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
                (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
                ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);
            var video = _encoders.ResolveVideo(videoCodec, allowHardware: true);
            var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);

            // Hardware-decode the playlist only when every item is the same resolution: the concat demuxer feeds
            // one shared decoder, and a mid-stream resolution change fails a hardware decoder (software handles
            // it fine). On any quick failure, drop to software decode for the rest of the session.
            var uniform = playable[0].SourceHeight > 0 && playable.All(p => p.SourceHeight == playable[0].SourceHeight);
            var decodeHwaccel = uniform ? video.DecodeHwaccel : null;

            // One continuous ffmpeg. If it dies after producing output (a bad file mid-playlist, an encoder
            // hiccup), restart at the current wall-clock position so the channel self-heals rather than going
            // silent. A near-instant exit means the pipeline itself is broken: show standby instead of spinning.
            // A run that keeps dying shortly after starting is the SAME failure on repeat (e.g. an undecodable
            // item at the current schedule position, which the restart re-seeks straight back into): after a few
            // strikes drop hardware decode, and after a few more stop thrashing and show standby, rather than
            // relaunching an encoder every few seconds for as long as the bad item stays on the schedule.
            var shortRuns = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var seek = SeekToNow(playable);
                var args = StreamArguments.BuildConcat(listFile, seek, width, bitrate, video, audioEncoder, audioBitrate, decodeHwaccel);

                var started = DateTime.UtcNow;
                var total = await RunFfmpegAsync(ffmpeg, args, channel.Name, output, cancellationToken, stats).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var ranFor = DateTime.UtcNow - started;
                var quickFail = total == 0 || ranFor < TimeSpan.FromSeconds(2);
                if (quickFail && decodeHwaccel is not null)
                {
                    // Hardware decode could not follow this playlist; fall back to software decode and retry
                    // rather than giving up.
                    _logger.LogWarning("Channel {Name}: hardware-decode continuous stream failed; falling back to software decode", channel.Name);
                    decodeHwaccel = null;
                    continue;
                }

                if (quickFail)
                {
                    _logger.LogWarning("Channel {Name}: continuous stream failed to run; showing standby", channel.Name);
                    await StreamSlateAsync(ffmpeg, TimeSpan.Zero, output, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (ranFor < TimeSpan.FromSeconds(60))
                {
                    shortRuns++;
                    if (shortRuns == 3 && decodeHwaccel is not null)
                    {
                        _logger.LogWarning("Channel {Name}: continuous stream keeps dying; retrying with software decode", channel.Name);
                        decodeHwaccel = null;
                        continue;
                    }

                    if (shortRuns >= 6)
                    {
                        _logger.LogWarning("Channel {Name}: continuous stream keeps dying; showing standby", channel.Name);
                        await StreamSlateAsync(ffmpeg, TimeSpan.Zero, output, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    // A healthy long run: earlier strikes were transient, not a stuck failure.
                    shortRuns = 0;
                }

                // Jellyfin keeps reading the same growing file, so a fresh ffmpeg resumes seamlessly at the
                // current wall-clock position.
                _logger.LogDebug("Channel {Name}: continuous stream ended; resuming at the current position", channel.Name);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(listFile))
                {
                    File.Delete(listFile);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete concat list {File}", listFile);
            }
        }
    }

    // The seek offset into the concatenated playlist for the current wall-clock position. Recomputed on each
    // (re)start so a resumed stream lands at "now", not the original tune-in point.
    private static TimeSpan SeekToNow(List<ProgramEntry> playable)
    {
        var (index, intoItem) = ScheduleCalculator.CurrentProgram(playable, DateTime.UtcNow, ScheduleCalculator.Epoch);
        var seek = intoItem;
        for (var i = 0; i < index; i++)
        {
            seek += TimeSpan.FromTicks(playable[i].DurationTicks);
        }

        return seek;
    }

    // Streams item-by-item (one ffmpeg per item, timestamps stitched with -output_ts_offset). Used only for
    // subtitle burn-in, high-resolution, HDR, or GPU-upload encoders, which need a per-item pipeline the concat
    // path cannot provide. Every item cold-starts at its boundary; the wall clock a cold start costs is healed by
    // that producer's deficit burst, and the viewer rides the tune-in head start well behind the live edge, so
    // boundaries stay invisible without ever running two encoders at once.
    private async Task StreamPerItemLoopAsync(string ffmpeg, Channel channel, IReadOnlyList<ProgramEntry> programs, int index, TimeSpan offset, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        var startOffset = offset;
        var consecutiveFailures = 0;
        var itemsPlayed = 0;
        var timeline = TimeSpan.Zero;

        // The session's wall-clock anchor for catch-up bursts. Each per-item ffmpeg re-anchors its -readrate
        // schedule at its own start, so time lost BETWEEN processes (cold starts, retries) never self-heals at
        // realtime. Every producer therefore bursts the session's current deficit against this anchor: the first
        // item's deficit is exactly the full head start, later items' exactly the wall clock the boundaries lost.
        var sessionStart = DateTime.UtcNow;
        double BurstNow() => StreamArguments.BurstForDeficit(DateTime.UtcNow - sessionStart, timeline);

        while (!cancellationToken.IsCancellationRequested)
        {
            var program = programs[index];
            _logger.LogDebug("Channel {Name}: item {Index}/{Count} \"{Title}\"", channel.Name, index + 1, programs.Count, program.Title);

            var streamed = await StreamItemAsync(ffmpeg, channel, program, startOffset, timeline, output, BurstNow, cancellationToken, stats).ConfigureAwait(false);

            if (streamed)
            {
                // Advance the timeline only by what actually played, so skipped items don't desync the offset.
                var played = TimeSpan.FromTicks(program.DurationTicks) - startOffset;
                timeline += played > TimeSpan.Zero ? played : TimeSpan.Zero;
                itemsPlayed++;
                consecutiveFailures = 0;
            }
            else if (++consecutiveFailures >= Math.Min(programs.Count, MaxConsecutiveFailures))
            {
                // Enough distinct items failed in a row that the storage is almost certainly the problem, not
                // the items: show standby now instead of walking a huge playlist (the cap keeps a 500-item
                // channel from spinning through spawn-and-fail for minutes before giving the viewer anything).
                _logger.LogWarning("{Count} consecutive item(s) on channel {Name} failed to play; showing standby", consecutiveFailures, channel.Name);
                await StreamSlateAsync(ffmpeg, timeline, output, cancellationToken).ConfigureAwait(false);
                break;
            }
            else
            {
                // A missing item returns instantly; pause briefly so a run of them can't busy-spin the CPU.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            startOffset = TimeSpan.Zero;
            index = (index + 1) % programs.Count;
        }

        _logger.LogDebug("Channel {Name}: stream ended after {Items} item(s)", channel.Name, itemsPlayed);
    }

    // Logs, once per tune-in, exactly how the channel will be encoded — resolution, codec, the resolved
    // hardware/software encoder, and whether decoding is hardware-assisted — so the active pipeline is visible
    // in the logs without enabling debug output.
    private void LogEncodePlan(Channel channel, int itemCount, int startIndex, bool perItem, bool uniform, bool hasHdr)
    {
        var (width, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c => (c.TranscodeWidth, c.VideoCodec, c.AudioCodec))
            ?? (1280, Models.VideoCodec.H264, Models.AudioCodec.Aac);
        var height = (int)Math.Round(width * 9.0 / 16.0);
        var profile = _encoders.ResolveVideo(videoCodec, allowHardware: true);
        var (audioEncoder, _) = EncoderResolver.ResolveAudio(audioCodec);

        // Decode follows the server's hardware acceleration. The decode plan is decided PER ITEM, so the label
        // must not overstate the channel-level flags: on the GPU-resident pipeline subtitle burn-in stays on the
        // GPU (overlay_vaapi composite); elsewhere burn-in and HDR route items to software decode. The continuous
        // pipeline hardware-decodes only when every item is the same resolution, and every path falls back to
        // software when a hardware decode fails.
        var burnIn = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never;
        var pipeline = perItem ? "per-item" : "continuous";
        var burnInForcesSoftware = burnIn && !string.IsNullOrEmpty(profile.DecodeDownload);
        var intelHardware = profile.Name.Contains("qsv", StringComparison.Ordinal) || profile.Name.Contains("vaapi", StringComparison.Ordinal);
        var gpuCapable = intelHardware && !string.IsNullOrEmpty(profile.GpuDevice);
        var hwDecode = !hasHdr && profile.DecodeHwaccel is not null && (perItem ? !burnInForcesSoftware : uniform);
        string decode;
        if (perItem && gpuCapable)
        {
            decode = burnIn ? "vaapi (GPU-resident, subtitles composited on GPU)" : "vaapi (GPU-resident)";
        }
        else if (hasHdr)
        {
            decode = "software (HDR)";
        }
        else
        {
            decode = hwDecode ? profile.DecodeHwaccel! : "software";
        }

        _logger.LogInformation(
            "Live Channels: streaming {Name} ({Items} items) at {Width}x{Height} via {Encoder} ({Mode} encode, {Decode} decode, {Pipeline}), audio {Audio}, from item {Index}/{Items}",
            channel.Name,
            itemCount,
            width,
            height,
            profile.Name,
            profile.IsHardware ? "hardware" : "software",
            decode,
            pipeline,
            audioEncoder,
            startIndex + 1,
            itemCount);
    }

    // Streams one item to the output, cold-starting its ffmpeg. The deficit burst is a callback so it is
    // evaluated at the moment each producer actually starts (the software retry re-evaluates it: the failed
    // hardware attempt burned wall clock that retry should recover).
    private async Task<bool> StreamItemAsync(string ffmpeg, Channel channel, ProgramEntry program, TimeSpan offset, TimeSpan timeline, Stream output, Func<double> burstSeconds, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        if (string.IsNullOrEmpty(program.Path) || !File.Exists(program.Path))
        {
            _logger.LogWarning("Skipping missing media for {Title}", program.Title);
            return false;
        }

        var subtitle = ChannelService.FindBurnInSubtitle(program, channel.SubtitleBurnIn);

        // Burning a text subtitle uses the libass `subtitles` filter, which reads the media file from the start
        // to reach the current position -- fatal on a deep tune-in seek into a multi-GB file (it scans gigabytes
        // and the producer stalls). So on the partial tune-in item, burn a small pre-extracted subtitle file
        // instead (Jellyfin extracts and caches it). If it is not cached yet, skip the burn for this one tune-in;
        // it warms the cache for next time. Full items from offset 0 and bitmap subtitles are unaffected.
        string? subtitlePath = null;
        if (subtitle.HasValue && subtitle.Value.IsText && offset > TimeSpan.Zero)
        {
            subtitlePath = await _channels.TryExtractTuneInSubtitleAsync(program.ItemId, subtitle.Value.RelativeIndex, offset, _subtitleRoot, cancellationToken).ConfigureAwait(false);
            if (subtitlePath is null)
            {
                _logger.LogDebug("Channel {Name}: tune-in subtitle not ready; skipping it on this item", channel.Name);
                subtitle = null;
            }
        }

        var (args, hardwareDecode) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: false, program.IsHdr, program.DefaultAudioOrdinal, burstSeconds());
        var total = await RunFfmpegAsync(ffmpeg, args, program.Title, output, cancellationToken, stats).ConfigureAwait(false);

        // The per-item path has no continuous decoder to fall back, so retry a hardware-decode that produced
        // nothing (e.g. a codec the GPU can't decode) once in software, so one bad item can't blank the channel.
        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: true, program.IsHdr, program.DefaultAudioOrdinal, burstSeconds());
            total = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken, stats).ConfigureAwait(false);
        }

        return total > 0;
    }

    // Streams a standby slate (colour bars + silence), looped, until the client disconnects. Shown when a
    // channel has no playable content so viewers get an intentional standby card, not a black screen. Starts on
    // the channel timeline where the caller left off (after any items that did play), so timestamps never regress.
    private async Task StreamSlateAsync(string ffmpeg, TimeSpan timelineBase, Stream output, CancellationToken cancellationToken)
    {
        var (width, bitrate) = Plugin.Instance?.ReadConfiguration(c => (c.TranscodeWidth, c.TranscodeVideoBitrateKbps))
            ?? (1280, 4000);
        const double clipSeconds = 10.0;

        var font = FontLocator.Find();
        var timeline = timelineBase;
        while (!cancellationToken.IsCancellationRequested)
        {
            var args = StreamArguments.BuildSlate(width, bitrate, clipSeconds, timeline, font);
            var started = DateTime.UtcNow;
            var total = await RunFfmpegAsync(ffmpeg, args, "standby", output, cancellationToken).ConfigureAwait(false);
            if (total == 0)
            {
                break; // ffmpeg could not even produce the slate; avoid a hot loop.
            }

            // If a clip ended far faster than its length (e.g. ffmpeg failing fast with some output), back off
            // briefly so the loop can't spin.
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(clipSeconds / 2))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            timeline += TimeSpan.FromSeconds(clipSeconds);
        }
    }

    // Runs ffmpeg with the given arguments, pumping its stdout to the output stream. Returns the bytes
    // written; zero means the item produced nothing (a real failure), which is logged with ffmpeg's stderr.
    private async Task<long> RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, string label, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        var (process, stderrTask) = StartProducer(ffmpeg, args, label, stats, cancellationToken);
        if (process is null)
        {
            return 0;
        }

        return await PumpProducerAsync(process, stderrTask, label, output, cancellationToken, stats).ConfigureAwait(false);
    }

    // Starts a producer ffmpeg and immediately begins draining its stderr (so it can never block on a full stderr
    // pipe), but does NOT read its stdout; PumpProducerAsync does that. Returns a null process if it could not start.
    private (Process? Process, Task<string> Stderr) StartProducer(string ffmpeg, IReadOnlyList<string> args, string label, SessionStats? stats, CancellationToken cancellationToken)
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

        // The exact producer command, at debug level: enable debug logging for the plugin to diagnose a
        // failure on a specific source (the downstream transcoder error only ever says the temp file was empty).
        _logger.LogDebug("Live Channels: producer ffmpeg [{Label}]: {Ffmpeg} {Args}", label, ffmpeg, string.Join(' ', args));

        // And into the session's reviewable ffmpeg log for the Sessions tab.
        stats?.AppendLog(label + "\n$ ffmpeg " + string.Join(' ', args));

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ffmpeg for {Label}", label);
            process.Dispose();
            return (null, Task.FromResult(string.Empty));
        }

        return (process, ReadStandardErrorAsync(process, stats, cancellationToken));
    }

    // Pumps a started producer's stdout into the output stream until it ends, then kills and disposes it.
    private async Task<long> PumpProducerAsync(Process process, Task<string> stderrTask, string label, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        long total = 0;
        var buffer = new byte[BufferSize];
        try
        {
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client went away; stop quietly.
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Stream write ended for {Label}", label);
            }
            finally
            {
                await KillAndWaitAsync(process).ConfigureAwait(false);
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            if (total == 0 && !cancellationToken.IsCancellationRequested)
            {
                // Output of nothing is a real failure (e.g. ffmpeg rejected a stream); surface its error and code.
                _logger.LogWarning("ffmpeg produced no output for {Label} (exit {Exit}): {Error}", label, exitCode, stderr.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg ({Label}, {Bytes} bytes, exit {Exit}): {Error}", label, total, exitCode, stderr.Trim());
            }

            // The exit summary (and any stderr tail) joins the session's reviewable ffmpeg log.
            var tail = stderr.Trim();
            stats?.AppendLog(label + ": exit " + exitCode.ToString(CultureInfo.InvariantCulture)
                + ", " + total.ToString(CultureInfo.InvariantCulture) + " bytes"
                + (tail.Length > 0 ? "\n" + tail : string.Empty));
        }
        finally
        {
            process.Dispose();
        }

        return total;
    }

    /// <summary>
    /// Builds the exact production producer arguments for one item, bounded to a fixed slice of content. The
    /// stress test runs N of these concurrently — the same command a real channel runs, pacing included — to
    /// measure how many simultaneous streams the server sustains at full frame rate.
    /// </summary>
    /// <param name="path">The media file path.</param>
    /// <param name="sourceHeight">The video height, driving the same decode decisions a channel makes.</param>
    /// <param name="isHdr">Whether the item is HDR (PQ/HLG).</param>
    /// <param name="offset">Seek offset into the item, so concurrent copies read different sections.</param>
    /// <param name="duration">How much content to encode.</param>
    /// <returns>The ffmpeg argument list and whether it hardware-decodes.</returns>
    public (List<string> Args, bool HardwareDecode) BuildStressArguments(string path, int sourceHeight, bool isHdr, TimeSpan offset, TimeSpan duration)
        => BuildArguments(path, offset, TimeSpan.Zero, null, sourceHeight, null, softwareDecode: false, isHdr, null, initialBurstSeconds: 0, duration);

    private (List<string> Args, bool HardwareDecode) BuildArguments(string path, TimeSpan offset, TimeSpan timeline, (int RelativeIndex, bool IsText)? forcedSubtitle, int sourceHeight, string? externalSubtitlePath, bool softwareDecode, bool isHdr, int? audioOrdinal, double initialBurstSeconds, TimeSpan? durationLimit = null)
    {
        // Read just the scalars we need inside the config lock, rather than holding a reference to the live
        // (mutable, shared) configuration object after the lock releases.
        var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);

        // The hardware encoder still applies to burn-in; only the decode side cares about the overlay -- and
        // the GPU-resident Intel pipeline composites the overlay on the GPU (overlay_vaapi), so burn-in there
        // keeps full hardware. Elsewhere, burn-in at 1080p-or-below drops to the software encoder (the CPU
        // composite dominates anyway); above 1080p the hardware encoder stays worth it.
        var hwProfile = _encoders.ResolveVideo(videoCodec, allowHardware: true);
        var hwIsIntelGpu = (hwProfile.Name.Contains("qsv", StringComparison.Ordinal) || hwProfile.Name.Contains("vaapi", StringComparison.Ordinal))
            && !string.IsNullOrEmpty(hwProfile.GpuDevice);
        var allowHardware = !forcedSubtitle.HasValue || sourceHeight > 1080 || hwIsIntelGpu;
        var video = allowHardware ? hwProfile : _encoders.ResolveVideo(videoCodec, allowHardware: false);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
        var intelHardware = video.Name.Contains("qsv", StringComparison.Ordinal) || video.Name.Contains("vaapi", StringComparison.Ordinal);

        // Intel with a known render node (Linux) runs the fully GPU-resident pipeline (built inside Build):
        // decode, deinterlace, scale, tone map, subtitle overlay, and encode all in VRAM, for SDR, 10-bit,
        // interlaced, and HDR alike.
        var intelGpu = intelHardware && !string.IsNullOrEmpty(video.GpuDevice) && !softwareDecode;

        // Outside the GPU-resident pipeline, HDR tone-maps in software (the zscale chain needs software-decoded
        // frames so the 10-bit depth survives), and burn-in forces software when the decoder would keep frames
        // in VRAM (the CPU overlay needs system frames). The caller forces software for the per-item retry.
        var hdrNeedsSoftware = isHdr && !intelGpu;
        var forceSoftware = softwareDecode || hdrNeedsSoftware
            || (forcedSubtitle.HasValue && !intelGpu && !string.IsNullOrEmpty(video.DecodeDownload));

        // Every hardware pipeline reports as such, so a no-output failure retries the item in software
        // (StreamItemAsync) instead of blanking the channel.
        var hardwareDecode = intelGpu || (!forceSoftware && !string.IsNullOrEmpty(video.DecodeHwaccel));
        var args = StreamArguments.Build(path, offset, timeline, width, bitrate, video, audioEncoder, audioBitrate, forcedSubtitle, externalSubtitlePath, forceSoftware, isHdr, audioOrdinal, initialBurstSeconds, durationLimit);
        return (args, hardwareDecode);
    }

    private static async Task<string> ReadStandardErrorAsync(Process process, SessionStats? stats, CancellationToken cancellationToken)
    {
        // Without a stats sink, the whole of stderr is only needed once, at exit, for diagnostics.
        if (stats is null)
        {
            try
            {
                return await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        // With a stats sink, read incrementally so the live encode speed can be parsed from ffmpeg's progress
        // lines (each terminated by a carriage return) while a long-running producer is still going. Only a
        // bounded tail of stderr is kept for diagnostics, so memory stays flat over a multi-hour stream.
        var tail = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[512];
        var tracker = new SpeedTracker();
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c == '\r' || c == '\n')
                    {
                        FlushStderrLine(line, tail, stats, tracker);
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }

            FlushStderrLine(line, tail, stats, tracker);
        }
        catch (OperationCanceledException)
        {
            // Cancelled on close; the tail collected so far is enough.
        }
        catch (IOException)
        {
            // The pipe closed as ffmpeg exited; the tail collected so far is enough.
        }

        return tail.ToString();
    }

    // Completes one stderr line: feeds it to the speed tracker, appends it to a bounded diagnostic tail, and
    // resets the line builder. The tail is trimmed from the front so it never grows past a few progress lines.
    private static void FlushStderrLine(StringBuilder line, StringBuilder tail, SessionStats stats, SpeedTracker tracker)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        tracker.Observe(text, stats);

        tail.Append(text).Append('\n');
        const int MaxTail = 4096;
        if (tail.Length > MaxTail)
        {
            tail.Remove(0, tail.Length - MaxTail);
        }
    }

    // Computes a live encode speed from ffmpeg's -progress output. ffmpeg's own "speed=" field is a cumulative
    // average since the stream began, so it barely moves once a stream has run a while (and the start-up burst
    // skews it for a long time). Instead this measures the content time advanced (out_time_us) between progress
    // blocks against wall-clock, giving an instantaneous rate that actually tracks whether the box keeps up.
    // One instance per producer process, lightly smoothed so the reading stays steady. Producers run one at a
    // time per session, so only ever one tracker writes the shared SessionStats.
    private sealed class SpeedTracker
    {
        private double _prevContentSeconds = -1;
        private long _prevStamp;

        public void Observe(string line, SessionStats stats)
        {
            const string key = "out_time_us=";
            if (!line.StartsWith(key, StringComparison.Ordinal))
            {
                return;
            }

            var value = line.AsSpan(key.Length).Trim();
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) || micros < 0)
            {
                // "out_time_us=N/A" before the first frame, or a malformed block: keep the previous reading.
                return;
            }

            var contentSeconds = micros / 1_000_000.0;
            // A monotonic timestamp, so an NTP/VM clock step cannot skew the elapsed time into a false speed.
            var nowStamp = Stopwatch.GetTimestamp();

            if (_prevContentSeconds >= 0)
            {
                var elapsed = (nowStamp - _prevStamp) / (double)Stopwatch.Frequency;
                var advanced = contentSeconds - _prevContentSeconds;
                if (elapsed >= 0.05 && advanced >= 0)
                {
                    var instant = advanced / elapsed;
                    var previous = stats.Speed;
                    // Exponential smoothing keeps the number from flickering between progress blocks.
                    stats.Speed = previous > 0 ? (previous * 0.6) + (instant * 0.4) : instant;
                }
            }

            _prevContentSeconds = contentSeconds;
            _prevStamp = nowStamp;
        }
    }

    // Kills the ffmpeg process tree and waits for it to actually exit. Kill() only signals; without waiting,
    // the encoder child can linger (still burning CPU) past the next item's spawn, piling up transcodes.
    private async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill ffmpeg process");
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffmpeg did not exit promptly after kill");
        }
    }
}
