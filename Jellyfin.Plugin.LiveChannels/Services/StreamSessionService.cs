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
            await StreamSlateAsync(ffmpeg, output, cancellationToken).ConfigureAwait(false);
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

        // HDR detection is a per-item media-stream query, so scanning a whole channel for it is the slowest part of
        // a large channel's start-up (a 3000+ item channel stalled for over a minute on an N100). It only matters
        // when it could flip an otherwise-concat channel to per-item, so skip the entire scan when the channel is
        // already per-item for another reason: subtitle burn-in, a >1080p item, or a GPU-upload encoder (QSV/VAAPI).
        var alreadyPerItem = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never || highRes || usesHwUpload;
        var hasHdr = !alreadyPerItem && programs.Any(p => _channels.IsHdrSource(p.ItemId));
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

        var windowMinutes = Plugin.Instance?.ReadConfiguration(c => c.StreamWindowMinutes) ?? 5;
        var listSize = StreamArguments.SegmentsForWindow(windowMinutes);
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

        // The segmenter only copies; its speed is never the bottleneck, so it needs no stats sink or speed gate.
        var stderrTask = ReadStandardErrorAsync(segmenter, null, null, cancellationToken);
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
            await StreamSlateAsync(ffmpeg, output, cancellationToken).ConfigureAwait(false);
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

            var (width, bitrate, videoCodec, audioCodec, readRateValue) = Plugin.Instance?.ReadConfiguration(c =>
                (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec, c.StreamReadRate))
                ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac, 1.0);
            var video = _encoders.ResolveVideo(videoCodec, allowHardware: true);
            var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);
            var readRate = StreamArguments.FormatReadRate(readRateValue);

            // Hardware-decode the playlist only when every item is the same resolution: the concat demuxer feeds
            // one shared decoder, and a mid-stream resolution change fails a hardware decoder (software handles
            // it fine). On any quick failure, drop to software decode for the rest of the session.
            var uniform = playable[0].SourceHeight > 0 && playable.All(p => p.SourceHeight == playable[0].SourceHeight);
            var decodeHwaccel = uniform ? video.DecodeHwaccel : null;

            // One continuous ffmpeg. If it dies after producing output (a bad file mid-playlist, an encoder
            // hiccup), restart at the current wall-clock position so the channel self-heals rather than going
            // silent. A near-instant exit means the pipeline itself is broken: show standby instead of spinning.
            while (!cancellationToken.IsCancellationRequested)
            {
                var seek = SeekToNow(playable);
                var args = StreamArguments.BuildConcat(listFile, seek, width, bitrate, video, audioEncoder, audioBitrate, decodeHwaccel, readRate);

                var started = DateTime.UtcNow;
                var total = await RunFfmpegAsync(ffmpeg, args, channel.Name, output, cancellationToken, stats).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var quickFail = total == 0 || DateTime.UtcNow - started < TimeSpan.FromSeconds(2);
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
                    await StreamSlateAsync(ffmpeg, output, cancellationToken).ConfigureAwait(false);
                    break;
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

    // How long before a full item finishes to warm up the next item's ffmpeg, so its cold start (open input,
    // init the decoder/encoder/HW context, decode the first frames) overlaps the tail of the current item and
    // the boundary has no gap. The warmed process blocks on its full stdout pipe after priming, so it cannot run
    // ahead of realtime and lurch the live edge.
    private static readonly TimeSpan WarmLead = TimeSpan.FromSeconds(3);

    // A next item whose ffmpeg is already started and primed (its stdout held closed by pipe backpressure),
    // ready to splice the instant the current item ends. Tied to the schedule position it was built for so a
    // failed or skipped current item never splices a mismatched warm item.
    private sealed record WarmItem(Process Process, Task<string> Stderr, int Index, TimeSpan Timeline, bool HardwareDecode, SpeedGate Gate, ProgramEntry Program);

    // Streams item-by-item (one ffmpeg per item, timestamps stitched with -output_ts_offset). Used only for
    // subtitle burn-in, high-resolution, HDR, or GPU-upload encoders, which need a per-item pipeline the concat
    // path cannot provide. Each full item warms the next one while it plays, so boundaries are seamless.
    private async Task StreamPerItemLoopAsync(string ffmpeg, Channel channel, IReadOnlyList<ProgramEntry> programs, int index, TimeSpan offset, Stream output, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        var startOffset = offset;
        var consecutiveFailures = 0;
        var timeline = TimeSpan.Zero;
        var itemsPlayed = 0;
        WarmItem? warm = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var program = programs[index];
                _logger.LogDebug("Channel {Name}: item {Index}/{Count} \"{Title}\"", channel.Name, index + 1, programs.Count, program.Title);

                bool streamed;
                if (startOffset > TimeSpan.Zero)
                {
                    // A partial tune-in item seeks into the file and may extract a subtitle first, so it runs cold
                    // through the existing path. Any warm item was built for a different position; drop it.
                    if (warm is not null)
                    {
                        await DiscardProducerAsync(warm.Process, warm.Stderr).ConfigureAwait(false);
                        warm = null;
                    }

                    streamed = await StreamItemAsync(ffmpeg, channel, program, startOffset, timeline, output, initialBurst: itemsPlayed == 0, cancellationToken, stats).ConfigureAwait(false);
                }
                else
                {
                    // A full item: splice the warmed producer if it matches where we actually are, and warm the
                    // next one as this plays.
                    WarmItem? ready = null;
                    if (warm is not null)
                    {
                        if (warm.Index == index && warm.Timeline == timeline)
                        {
                            ready = warm;
                        }
                        else
                        {
                            await DiscardProducerAsync(warm.Process, warm.Stderr).ConfigureAwait(false);
                        }

                        warm = null;
                    }

                    var contentDuration = TimeSpan.FromTicks(program.DurationTicks);
                    var nextIndex = (index + 1) % programs.Count;
                    var nextProgram = programs[nextIndex];
                    var nextTimeline = timeline + (contentDuration > TimeSpan.Zero ? contentDuration : TimeSpan.Zero);

                    var result = await PlayFullItemAsync(
                        ffmpeg, channel, program, timeline, contentDuration, ready, itemsPlayed == 0,
                        () => TryWarmNext(ffmpeg, channel, nextProgram, nextIndex, nextTimeline, stats, cancellationToken),
                        output, stats, cancellationToken).ConfigureAwait(false);
                    streamed = result.Streamed;
                    warm = result.Warmed;
                }

                if (streamed)
                {
                    // Advance the timeline only by what actually played, so skipped items don't desync the offset.
                    var played = TimeSpan.FromTicks(program.DurationTicks) - startOffset;
                    timeline += played > TimeSpan.Zero ? played : TimeSpan.Zero;
                    itemsPlayed++;
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= programs.Count)
                {
                    if (warm is not null)
                    {
                        await DiscardProducerAsync(warm.Process, warm.Stderr).ConfigureAwait(false);
                        warm = null;
                    }

                    _logger.LogWarning("Every item on channel {Name} failed to play; showing standby", channel.Name);
                    await StreamSlateAsync(ffmpeg, output, cancellationToken).ConfigureAwait(false);
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
        }
        finally
        {
            if (warm is not null)
            {
                await DiscardProducerAsync(warm.Process, warm.Stderr).ConfigureAwait(false);
            }
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

        // Decode follows the server's hardware acceleration. The per-item path hardware-decodes each item, except
        // subtitle burn-in on a GPU-resident decoder (QSV/VAAPI), which composites in software. The continuous
        // pipeline hardware-decodes only when every item is the same resolution. Either way, software is used when
        // no hardware decoder is configured (and the paths fall back to software if a hardware decode fails).
        var burnIn = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never;
        var pipeline = perItem ? "per-item" : "continuous";
        var burnInForcesSoftware = burnIn && !string.IsNullOrEmpty(profile.DecodeDownload);
        var intelHardware = profile.Name.Contains("qsv", StringComparison.Ordinal) || profile.Name.Contains("vaapi", StringComparison.Ordinal);
        // On Intel the per-item path runs entirely on the GPU (VAAPI decode/deinterlace/tone-map/scale, then the
        // QSV or VAAPI encoder), except subtitle burn-in, which composites in software. Other hardware decoders
        // assist the per-item path (continuous only when every item is the same resolution); software otherwise.
        var intelGpu = intelHardware && perItem && !burnIn;
        var hwDecode = !intelHardware && !hasHdr && profile.DecodeHwaccel is not null && (perItem ? !burnInForcesSoftware : uniform);
        var decode = intelGpu
            ? (hasHdr ? "vaapi (HDR)" : "vaapi")
            : (hwDecode ? profile.DecodeHwaccel! : "software");

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

    private async Task<bool> StreamItemAsync(string ffmpeg, Channel channel, ProgramEntry program, TimeSpan offset, TimeSpan timeline, Stream output, bool initialBurst, CancellationToken cancellationToken, SessionStats? stats = null)
    {
        if (string.IsNullOrEmpty(program.Path) || !File.Exists(program.Path))
        {
            _logger.LogWarning("Skipping missing media for {Title}", program.Title);
            return false;
        }

        var subtitle = _channels.FindBurnInSubtitle(program.ItemId, channel.SubtitleBurnIn);

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

        var isHdr = _channels.IsHdrSource(program.ItemId);
        var isInterlaced = _channels.IsInterlacedSource(program.ItemId);
        var audioOrdinal = _channels.GetDefaultAudioOrdinal(program.ItemId);
        var (args, hardwareDecode) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: false, isHdr, isInterlaced, audioOrdinal, initialBurst);
        var total = await RunFfmpegAsync(ffmpeg, args, program.Title, output, cancellationToken, stats).ConfigureAwait(false);

        // The per-item path has no continuous decoder to fall back, so retry a hardware-decode that produced
        // nothing (e.g. a codec the GPU can't decode) once in software, so one bad item can't blank the channel.
        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: true, isHdr, isInterlaced, audioOrdinal, initialBurst);
            total = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken, stats).ConfigureAwait(false);
        }

        return total > 0;
    }

    // Builds the producer arguments for a full (offset 0) item. Burn-in subtitles are resolved synchronously
    // here -- the async tune-in subtitle extraction only applies to a partial seek -- so the next item can be
    // warmed without awaiting. Returns whether the args hardware-decode, so a no-output result can retry in software.
    private (List<string> Args, bool HardwareDecode) BuildItemArgs(Channel channel, ProgramEntry program, TimeSpan timeline, bool softwareDecode, bool initialBurst)
    {
        var subtitle = _channels.FindBurnInSubtitle(program.ItemId, channel.SubtitleBurnIn);
        var isHdr = _channels.IsHdrSource(program.ItemId);
        var isInterlaced = _channels.IsInterlacedSource(program.ItemId);
        var audioOrdinal = _channels.GetDefaultAudioOrdinal(program.ItemId);
        return BuildArguments(program.Path!, TimeSpan.Zero, timeline, subtitle, program.SourceHeight, null, softwareDecode, isHdr, isInterlaced, audioOrdinal, initialBurst);
    }

    // Starts and primes the next full item so it is ready to splice with no cold-start gap. A missing file cannot
    // be warmed (the cold loop will skip it); returns null then, or if ffmpeg could not start.
    private WarmItem? TryWarmNext(string ffmpeg, Channel channel, ProgramEntry program, int index, TimeSpan timeline, SessionStats? stats, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrEmpty(program.Path) || !File.Exists(program.Path))
        {
            return null;
        }

        var (args, hardwareDecode) = BuildItemArgs(channel, program, timeline, softwareDecode: false, initialBurst: false);
        // The gate starts closed so this warming producer does not write the shared speed while the current item
        // is still the one on screen; it opens when this item is spliced in.
        var gate = new SpeedGate();
        var (process, stderr) = StartProducer(ffmpeg, args, program.Title, stats, gate, cancellationToken);
        if (process is null)
        {
            return null;
        }

        _logger.LogDebug("Channel {Name}: warming next item \"{Title}\"", channel.Name, program.Title);
        return new WarmItem(process, stderr, index, timeline, hardwareDecode, gate, program);
    }

    // Plays one full item to the output: pumps the warmed producer (or cold-starts it), and once the item is
    // within WarmLead of finishing, warms the next item so the boundary is seamless. Retries a no-output hardware
    // decode in software, exactly like the cold path. Returns whether anything played and the warmed next item.
    private async Task<(bool Streamed, WarmItem? Warmed)> PlayFullItemAsync(
        string ffmpeg, Channel channel, ProgramEntry program, TimeSpan timeline, TimeSpan contentDuration,
        WarmItem? ready, bool initialBurst, Func<WarmItem?> warmNext, Stream output, SessionStats? stats, CancellationToken cancellationToken)
    {
        Process process;
        Task<string> stderr;
        bool hardwareDecode;
        if (ready is not null)
        {
            process = ready.Process;
            stderr = ready.Stderr;
            hardwareDecode = ready.HardwareDecode;
            // This warmed item is now the current one, so let its producer publish the live speed.
            ready.Gate.Active = true;
        }
        else
        {
            if (string.IsNullOrEmpty(program.Path) || !File.Exists(program.Path))
            {
                _logger.LogWarning("Skipping missing media for {Title}", program.Title);
                return (false, null);
            }

            var (args, hw) = BuildItemArgs(channel, program, timeline, softwareDecode: false, initialBurst);
            // A cold-started item is the current one immediately, so it publishes speed with no gate.
            var started = StartProducer(ffmpeg, args, program.Title, stats, speedGate: null, cancellationToken);
            if (started.Process is null)
            {
                return (false, null);
            }

            process = started.Process;
            stderr = started.Stderr;
            hardwareDecode = hw;
        }

        // Warm the next item once this one is within WarmLead of its end. The linked token cancels the wait if
        // this item ends early (a failure), so the loop is never held up by the timer. Items shorter than the lead
        // are not warmed: there is no tail to overlap, and warming at once would cold-start two producers together.
        using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        WarmItem? warmed = null;
        var warmDelay = contentDuration - WarmLead;
        var warmTask = warmDelay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(warmDelay, warmCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        // Best effort: a failure to warm the next item must never bubble up and kill the stream;
                        // the boundary just falls back to a cold start.
                        warmed = warmNext();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Live Channels: warming the next item failed");
                    }
                },
                CancellationToken.None);

        var total = await PumpProducerAsync(process, stderr, program.Title, output, cancellationToken).ConfigureAwait(false);
        await warmCts.CancelAsync().ConfigureAwait(false);
        await warmTask.ConfigureAwait(false);

        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            // Hardware decode produced nothing. Drop any warm item (its timeline assumption no longer holds) and
            // retry this item in software so one bad item cannot blank the channel.
            if (warmed is not null)
            {
                await DiscardProducerAsync(warmed.Process, warmed.Stderr).ConfigureAwait(false);
                warmed = null;
            }

            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildItemArgs(channel, program, timeline, softwareDecode: true, initialBurst);
            total = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken, stats).ConfigureAwait(false);
        }

        return (total > 0, warmed);
    }

    // Streams a standby slate (colour bars + silence), looped, until the client disconnects. Shown when a
    // channel has no playable content so viewers get an intentional standby card, not a black screen.
    private async Task StreamSlateAsync(string ffmpeg, Stream output, CancellationToken cancellationToken)
    {
        var (width, bitrate) = Plugin.Instance?.ReadConfiguration(c => (c.TranscodeWidth, c.TranscodeVideoBitrateKbps))
            ?? (1280, 4000);
        const double clipSeconds = 10.0;

        var font = FontLocator.Find();
        var timeline = TimeSpan.Zero;
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
        var (process, stderrTask) = StartProducer(ffmpeg, args, label, stats, speedGate: null, cancellationToken);
        if (process is null)
        {
            return 0;
        }

        return await PumpProducerAsync(process, stderrTask, label, output, cancellationToken).ConfigureAwait(false);
    }

    // Starts a producer ffmpeg and immediately begins draining its stderr (so it can never block on a full stderr
    // pipe), but does NOT read its stdout. A warmed next item is left in exactly this state: ffmpeg pays its
    // cold-start cost (open input, init the decoder/encoder/HW context, decode the first frames) and then blocks
    // on its full stdout pipe, so it is primed to splice into the live feed the instant the current item ends.
    // Returns a null process if it could not start.
    private (Process? Process, Task<string> Stderr) StartProducer(string ffmpeg, IReadOnlyList<string> args, string label, SessionStats? stats, SpeedGate? speedGate, CancellationToken cancellationToken)
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

        return (process, ReadStandardErrorAsync(process, stats, speedGate, cancellationToken));
    }

    // Pumps a started producer's stdout into the output stream until it ends, then kills and disposes it. Pairs
    // with StartProducer: the split lets the per-item loop start the next item early (warm it) and only begin
    // pumping it once the current item finishes.
    private async Task<long> PumpProducerAsync(Process process, Task<string> stderrTask, string label, Stream output, CancellationToken cancellationToken)
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
        }
        finally
        {
            process.Dispose();
        }

        return total;
    }

    // Kills, drains, and disposes a warmed producer that will not be used (the current item failed, the schedule
    // moved on, or the stream is closing), so a primed-but-unspliced ffmpeg never lingers.
    private async Task DiscardProducerAsync(Process process, Task<string> stderrTask)
    {
        try
        {
            await KillAndWaitAsync(process).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Channels: discarding a warmed producer failed");
        }
        finally
        {
            process.Dispose();
        }
    }

    private (List<string> Args, bool HardwareDecode) BuildArguments(string path, TimeSpan offset, TimeSpan timeline, (int RelativeIndex, bool IsText)? forcedSubtitle, int sourceHeight, string? externalSubtitlePath, bool softwareDecode, bool isHdr, bool isInterlaced, int? audioOrdinal, bool initialBurst)
    {
        // Read just the scalars we need inside the config lock, rather than holding a reference to the live
        // (mutable, shared) configuration object after the lock releases.
        var (width, bitrate, videoCodec, audioCodec, readRateValue) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec, c.StreamReadRate))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac, 1.0);
        var readRate = StreamArguments.FormatReadRate(readRateValue);

        // The hardware encoder still applies to burn-in; only the decode side cares about the overlay.
        var allowHardware = !forcedSubtitle.HasValue || sourceHeight > 1080;
        var video = _encoders.ResolveVideo(videoCodec, allowHardware);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);

        // On Intel (QSV/VAAPI) the whole pipeline runs on the GPU (decode, deinterlace, tone-map, scale, encode),
        // mirroring Jellyfin's own transcoder. Build handles the filter graph; here we only decide whether that
        // all-GPU path applies. It does for everything on Intel except subtitle burn-in (the libass overlay needs
        // system frames) and the per-item software-decode fallback. Interlaced and HDR sources stay on the GPU
        // (deinterlace_vaapi / tonemap_vaapi); they are no longer forced to software.
        var intelHardware = video.Name.Contains("qsv", StringComparison.Ordinal) || video.Name.Contains("vaapi", StringComparison.Ordinal);
        var useIntelGpu = intelHardware && !forcedSubtitle.HasValue && !softwareDecode;

        // HDR off the Intel GPU path (VideoToolbox/NVENC/software, plus burn-in and the fallback) tone-maps in
        // software, which needs software-decoded frames. Burn-in on a GPU-resident decoder also forces software.
        var hdrNeedsSoftware = isHdr && !useIntelGpu;
        var forceSoftware = softwareDecode || hdrNeedsSoftware || (forcedSubtitle.HasValue && !string.IsNullOrEmpty(video.DecodeDownload));

        // The Intel GPU path runs on hardware, so a failure should fall back to software exactly like a hardware
        // decode does (StreamItemAsync retries with softwareDecode).
        var hardwareDecode = useIntelGpu || (!forceSoftware && !string.IsNullOrEmpty(video.DecodeHwaccel));
        var args = StreamArguments.Build(path, offset, timeline, width, bitrate, video, audioEncoder, audioBitrate, forcedSubtitle, externalSubtitlePath, forceSoftware, isHdr, audioOrdinal, initialBurst, isInterlaced, readRate);
        return (args, hardwareDecode);
    }

    private static async Task<string> ReadStandardErrorAsync(Process process, SessionStats? stats, SpeedGate? speedGate, CancellationToken cancellationToken)
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
        var tracker = new SpeedTracker(speedGate);
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
    // average since the stream began, so it barely moves once a stream has run a while (and the concat path's
    // start-up burst skews it for a long time). Instead this measures the content time advanced (out_time_us)
    // between progress blocks against wall-clock, giving an instantaneous rate that actually tracks whether the
    // box keeps up. One instance per producer process, lightly smoothed so the reading stays steady.
    // Withholds speed updates from a warmed producer until it is spliced in as the current item. Without it the
    // warming and current producers would both write the one shared SessionStats during the warm-up overlap, and
    // the read-modify-write smoothing would blend the current item's true speed with the warming item's
    // primed-then-stalled rate, dipping the Sessions-tab reading well below realtime even when the box keeps up.
    private sealed class SpeedGate
    {
        public volatile bool Active;
    }

    private sealed class SpeedTracker
    {
        private readonly SpeedGate? _gate;
        private double _prevContentSeconds = -1;
        private long _prevStamp;

        public SpeedTracker(SpeedGate? gate) => _gate = gate;

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

            // Track timestamps even while gated, so the first reading after a splice is measured over one real
            // interval; only the write to the shared stats is withheld until this producer is the current one.
            if (_prevContentSeconds >= 0 && (_gate is null || _gate.Active))
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
