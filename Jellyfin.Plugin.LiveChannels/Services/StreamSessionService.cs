using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public async Task StreamToAsync(Channel channel, Stream output, CancellationToken cancellationToken)
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
        var hasHdr = programs.Any(p => _channels.IsHdrSource(p.ItemId));
        var perItem = channel.SubtitleBurnIn != Models.SubtitleBurnInMode.Never || highRes || hasHdr || usesHwUpload;
        var uniform = programs.Count > 0 && programs[0].SourceHeight > 0 && programs.All(p => p.SourceHeight == programs[0].SourceHeight);
        LogEncodePlan(channel, programs.Count, index, perItem, uniform, hasHdr);

        if (perItem)
        {
            await StreamPerItemLoopAsync(ffmpeg, channel, programs, index, offset, output, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StreamConcatAsync(ffmpeg, channel, programs, output, cancellationToken).ConfigureAwait(false);
        }
    }

    // Streams the channel as ONE continuous ffmpeg using the concat demuxer, so item boundaries are seamless
    // (no timestamp or continuity reset). Software decode (hardware decoders fail on the per-segment resolution
    // changes a playlist produces) with the hardware encoder.
    private async Task StreamConcatAsync(string ffmpeg, Channel channel, IReadOnlyList<ProgramEntry> programs, Stream output, CancellationToken cancellationToken)
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
            while (!cancellationToken.IsCancellationRequested)
            {
                var seek = SeekToNow(playable);
                var args = StreamArguments.BuildConcat(listFile, seek, width, bitrate, video, audioEncoder, audioBitrate, decodeHwaccel, "pipe:1");

                var started = DateTime.UtcNow;
                var total = await RunFfmpegAsync(ffmpeg, args, channel.Name, output, cancellationToken).ConfigureAwait(false);
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

    // Streams item-by-item (one ffmpeg per item, timestamps stitched with -output_ts_offset). Used whenever the
    // concat pipeline can't serve a channel: subtitle burn-in (needs a per-item filter graph), high-resolution or
    // HDR sources, and GPU-upload encoders (QSV/VAAPI) that can't survive the concat demuxer's filter reinit.
    private async Task StreamPerItemLoopAsync(string ffmpeg, Channel channel, IReadOnlyList<ProgramEntry> programs, int index, TimeSpan offset, Stream output, CancellationToken cancellationToken)
    {
        var startOffset = offset;
        var consecutiveFailures = 0;
        var timeline = TimeSpan.Zero;
        var itemsPlayed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var program = programs[index];
            _logger.LogDebug("Channel {Name}: item {Index}/{Count} \"{Title}\"", channel.Name, index + 1, programs.Count, program.Title);
            var streamed = await StreamItemAsync(ffmpeg, channel, program, startOffset, timeline, output, cancellationToken).ConfigureAwait(false);

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
        var hwDecode = !hasHdr && profile.DecodeHwaccel is not null && (perItem ? !burnInForcesSoftware : uniform);
        // HDR on Intel hardware tone-maps on the GPU (VAAPI); HDR elsewhere tone-maps in software.
        var decode = hasHdr
            ? (intelHardware && !burnIn ? "vaapi (HDR)" : "software (HDR)")
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

    private async Task<bool> StreamItemAsync(string ffmpeg, Channel channel, ProgramEntry program, TimeSpan offset, TimeSpan timeline, Stream output, CancellationToken cancellationToken)
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
        var isTenBit = _channels.IsTenBitSource(program.ItemId);
        var audioOrdinal = _channels.GetDefaultAudioOrdinal(program.ItemId);
        var (args, hardwareDecode) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: false, isHdr, isTenBit, audioOrdinal);
        var total = await RunFfmpegAsync(ffmpeg, args, program.Title, output, cancellationToken).ConfigureAwait(false);

        // The per-item path has no continuous decoder to fall back, so retry a hardware-decode that produced
        // nothing (e.g. a codec the GPU can't decode) once in software, so one bad item can't blank the channel.
        if (total == 0 && hardwareDecode && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Channel {Name}: hardware decode produced no output for \"{Title}\"; retrying in software", channel.Name, program.Title);
            var (swArgs, _) = BuildArguments(program.Path, offset, timeline, subtitle, program.SourceHeight, subtitlePath, softwareDecode: true, isHdr, isTenBit, audioOrdinal);
            total = await RunFfmpegAsync(ffmpeg, swArgs, program.Title, output, cancellationToken).ConfigureAwait(false);
        }

        return total > 0;
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
            var args = StreamArguments.BuildSlate(width, bitrate, clipSeconds, timeline, font, "pipe:1");
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
    private async Task<long> RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, string label, Stream output, CancellationToken cancellationToken)
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

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ffmpeg for {Label}", label);
            return 0;
        }

        var stderrTask = ReadStandardErrorAsync(process, cancellationToken);

        long total = 0;
        var buffer = new byte[BufferSize];
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

        return total;
    }

    private (List<string> Args, bool HardwareDecode) BuildArguments(string path, TimeSpan offset, TimeSpan timeline, (int RelativeIndex, bool IsText)? forcedSubtitle, int sourceHeight, string? externalSubtitlePath, bool softwareDecode, bool isHdr, bool isTenBit, int? audioOrdinal)
    {
        // Read just the scalars we need inside the config lock, rather than holding a reference to the live
        // (mutable, shared) configuration object after the lock releases.
        var (width, bitrate, videoCodec, audioCodec) = Plugin.Instance?.ReadConfiguration(c =>
            (c.TranscodeWidth, c.TranscodeVideoBitrateKbps, c.VideoCodec, c.AudioCodec))
            ?? (1280, 4000, Models.VideoCodec.H264, Models.AudioCodec.Aac);

        // The hardware encoder still applies to burn-in; only the decode side cares about the overlay.
        var allowHardware = !forcedSubtitle.HasValue || sourceHeight > 1080;
        var video = _encoders.ResolveVideo(videoCodec, allowHardware);
        var (audioEncoder, audioBitrate) = EncoderResolver.ResolveAudio(audioCodec);

        // HDR on an Intel hardware encoder (QSV/VAAPI) tone-maps on the GPU (handled inside Build) and must NOT be
        // forced to software. HDR anywhere else (VideoToolbox/NVENC/software) and HDR burn-in tone-map in software,
        // which needs software-decoded frames. Burn-in on a GPU-resident decoder also forces software (the overlay
        // needs system frames). The caller can force software for the per-item fallback.
        var intelHardware = video.Name.Contains("qsv", StringComparison.Ordinal) || video.Name.Contains("vaapi", StringComparison.Ordinal);
        var usesHdrVaapi = isHdr && intelHardware && !forcedSubtitle.HasValue && !softwareDecode;
        var hdrNeedsSoftware = isHdr && !usesHdrVaapi;
        // A 10-bit (non-HDR) source decoded on QSV/VAAPI produces a p010 surface, which the per-item path cannot
        // hwdownload to nv12 for the software scale (exit 234). Decode those items in software instead; the encode
        // stays on the GPU, so hardware acceleration is kept where it matters. HDR 10-bit keeps the GPU VAAPI path.
        var tenBitNeedsSoftware = isTenBit && !usesHdrVaapi && intelHardware;
        var forceSoftware = softwareDecode || hdrNeedsSoftware || tenBitNeedsSoftware || (forcedSubtitle.HasValue && !string.IsNullOrEmpty(video.DecodeDownload));

        // The HDR VAAPI path runs on hardware, so a failure should fall back to software exactly like a hardware
        // decode does (StreamItemAsync retries with softwareDecode).
        var hardwareDecode = usesHdrVaapi || (!forceSoftware && !string.IsNullOrEmpty(video.DecodeHwaccel));
        var args = StreamArguments.Build(path, offset, timeline, width, bitrate, video, audioEncoder, audioBitrate, forcedSubtitle, externalSubtitlePath, forceSoftware, isHdr, audioOrdinal);
        return (args, hardwareDecode);
    }

    private static async Task<string> ReadStandardErrorAsync(Process process, CancellationToken cancellationToken)
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
