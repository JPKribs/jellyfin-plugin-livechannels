using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Builds the ffmpeg argument list for a single item in a channel's stream. Pure (no Jellyfin dependencies)
/// so the transcode/burn-in argument shapes can be unit-tested directly.
/// </summary>
public static class StreamArguments
{
    // The producer always reads at exactly realtime (-readrate 1.0), feeding the HLS segmenter's rolling window
    // of small segments (the playlist) at the rate the player consumes it. A rate above realtime is never the
    // answer to falling behind: it advances the live edge faster than the player can follow, forever, until the
    // player falls off the back of the window as old segments are deleted. Catch-up is handled by bursts instead:
    //
    // Bursts are anchored to the session's wall clock. -readrate is a cumulative cap, so hiccups WITHIN one ffmpeg
    // self-heal (it reads full speed until back on its own schedule) -- but each per-item ffmpeg re-anchors that
    // schedule at its own start, so wall-clock time lost BETWEEN processes (cold starts, hardware-decode retries,
    // subtitle extraction) is unrecoverable at realtime and accumulates until the player catches the live edge and
    // stalls. The fix: the first item bursts the full head start, and every later item bursts exactly the session's
    // accumulated deficit (see BurstForDeficit), which restores the live edge to schedule without ever lurching it
    // past where a full-head-start session would be.
    private const string RealtimeReadRate = "1.0";
    private static readonly string GopFrames = (OutputFps * SegmentSeconds).ToString(CultureInfo.InvariantCulture);
    private const double MinBurstSeconds = 0.05;

    /// <summary>
    /// The tune-in head start in seconds: how much content the first item (or the single concat ffmpeg) bursts
    /// ahead of realtime before the player joins, and the cap on any later item's catch-up burst. It is the
    /// standing lead the producer keeps over the viewer for the life of the session.
    /// </summary>
    public const double InitialBurstSeconds = 30;

    /// <summary>
    /// The length of one HLS segment in seconds; the granularity of every buffer measured in segments. It matches
    /// the 3-second segments Jellyfin's own delivery remux cuts (and is an exact divisor of the 6-second ones it
    /// cuts for Apple players), and the encoder's GOP is one segment long, so every stage cuts on the same
    /// keyframes: the first segment closes as early as it can, and no stage ever waits for a keyframe the stage
    /// before it did not already cut on.
    /// </summary>
    public const int SegmentSeconds = 3;

    /// <summary>The fixed output frame rate every item is conformed to.</summary>
    public const int OutputFps = 30;

    // The HLS segmenter packages the producer's continuous TS into a rolling, self-trimming playlist of fixed
    // length segments. How many segments are retained (the window) is fixed by the caller (see
    // StreamSessionService.StreamToHlsAsync): it has to cover the tune-in head start plus the worst-case burst
    // advance of the self-heal path, and it is also the upper bound on disk use per active channel.
    private const int HlsSegmentSeconds = SegmentSeconds;

    // The portable software tone map (ffmpeg's zscale + tonemap), used when the server's ffmpeg has no tonemapx.
    // Hable keeps highlights soft without crushing the mid-tones; the chain ends in 8-bit bt709 4:2:0.
    private const string ZscaleToneMap =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    // How far audio may stray from its timestamps before aresample pads or drops samples to put it back. A per-item
    // producer starts fresh on every item, so nothing accumulates and the long-standing 100 ms leaves ordinary
    // timestamp jitter alone.
    private const double AudioResyncSeconds = 0.100;

    // The continuous concat producer is different: the demuxer times each next file from the previous file's
    // container duration, which rarely matches its audio exactly, so every item change leaves the audio a few
    // milliseconds off and, uncorrected below 100 ms, that builds up item after item. Measured over 12 item changes:
    // 100 ms let the audio drift to 86 ms early before snapping back (a sawtooth, over and over, through a long
    // session); 10 ms holds it within one video frame the whole way.
    private const double ConcatAudioResyncSeconds = 0.010;

    // Never keep fewer than this many segments, so even a tiny configured window leaves the player something to
    // work with on tune-in.
    private const int MinHlsSegments = 12;

    /// <summary>
    /// Builds the ffmpeg arguments for one item. Every item is re-encoded to one uniform MPEG-TS (in the
    /// configured video/audio codec) so the single continuous channel stream never changes format at an item
    /// boundary &mdash; the one shape a player can follow reliably across a linear feed.
    /// </summary>
    /// <param name="path">The media file path.</param>
    /// <param name="offset">Seek offset into the item (only the first item on tune-in is non-zero).</param>
    /// <param name="timeline">Output timestamp offset, continuing the channel's timeline.</param>
    /// <param name="width">Target output width.</param>
    /// <param name="bitrate">Target video bitrate in kbps.</param>
    /// <param name="video">The resolved video-encoder profile (software or hardware).</param>
    /// <param name="audioEncoder">The ffmpeg audio encoder name (e.g. <c>aac</c>, <c>ac3</c>, <c>eac3</c>).</param>
    /// <param name="audioBitrate">Target audio bitrate in kbps.</param>
    /// <param name="forcedSubtitle">The subtitle to burn in (relative index, is-text), or <c>null</c>.</param>
    /// <param name="externalSubtitlePath">The extracted subtitle file to burn (Jellyfin's own cached extraction), or <c>null</c> to read the track out of the media file.</param>
    /// <param name="softwareDecode">Whether to force software decoding.</param>
    /// <param name="isHdr">Whether the source carries an HDR transfer and must be tone-mapped.</param>
    /// <param name="audioOrdinal">The audio track to map, among the item's audio streams.</param>
    /// <param name="initialBurstSeconds">How much content to produce ahead of realtime before settling to the paced read rate.</param>
    /// <param name="durationLimit">How much content to read, or <c>null</c> for the whole item.</param>
    /// <param name="subtitleStyle">The libass <c>force_style</c> override for burned-in subtitles, or <c>null</c> to render them as authored.</param>
    /// <param name="subtitleFontsDir">A directory of fonts attached to the media, so an ASS subtitle renders in the font it was authored with, or <c>null</c>.</param>
    /// <param name="silentAudio">For a video-only item, how much silence to give it as its audio track (the length of video this run plays), or <c>null</c> to map the item's own audio.</param>
    /// <param name="toneMapStage">The software tone-map filter for an HDR item outside the GPU-resident pipeline (jellyfin-ffmpeg's tonemapx with the server's settings), or <c>null</c> for the portable zscale chain.</param>
    /// <param name="inputMatrix">The colour matrix the item's pixels are in (see <see cref="InputMatrix"/>), so an SDR item is converted to the channel's bt709; <c>auto</c> leaves it to ffmpeg.</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> Build(
        string path,
        TimeSpan offset,
        TimeSpan timeline,
        int width,
        int bitrate,
        VideoEncoderProfile video,
        string audioEncoder,
        int audioBitrate,
        (int RelativeIndex, bool IsText)? forcedSubtitle,
        string? externalSubtitlePath = null,
        bool softwareDecode = false,
        bool isHdr = false,
        int? audioOrdinal = null,
        double initialBurstSeconds = 0,
        TimeSpan? durationLimit = null,
        string? subtitleStyle = null,
        string? subtitleFontsDir = null,
        TimeSpan? silentAudio = null,
        string? toneMapStage = null,
        string inputMatrix = "auto")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        var burnIn = forcedSubtitle.HasValue;

        // The fully GPU-resident Intel pipeline: when the encoder is Intel (QSV/VAAPI) and Jellyfin's render
        // node is known (Linux), decode, deinterlace, scale, tone map, subtitle overlay, and encode all stay
        // in VRAM. Only the software-decode retry uses the CPU chains below.
        var intelGpu = IsIntelHardware(video.Name) && !softwareDecode && !string.IsNullOrEmpty(video.GpuDevice);
        var hwDecode = !intelGpu && !softwareDecode && !string.IsNullOrEmpty(video.DecodeHwaccel);

        // +genpts fills in any missing presentation timestamps so each item starts from a clean, monotonic
        // timeline before the offset is applied.
        // -progress writes machine-readable key=value progress (including speed=) to stderr even at loglevel
        // error, so the Sessions tab can report the live encode speed without the noisy human stats line.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-progress", "pipe:2", "-fflags", "+genpts" };

        // Hardware device initialisation goes before the input. The GPU-resident Intel pipeline binds Jellyfin's
        // configured render node explicitly (a device path is unambiguous on dual-Intel boxes, where vendor
        // matching is not) and, for a QSV encoder, derives the QSV device from it so filters and encoder
        // share frames. Everything else uses the profile's own init arguments.
        if (intelGpu)
        {
            Add(args, "-init_hw_device", "vaapi=va:" + video.GpuDevice);
            if (video.Name.Contains("qsv", StringComparison.Ordinal))
            {
                // Burn-in uploads the CPU-rendered subtitle picture with hwupload, which targets the filter
                // device — that must be the VAAPI device (overlay_vaapi composites there); the encoder still
                // receives QSV frames via the explicit hwmap at the end of the graph.
                Add(args, "-init_hw_device", "qsv=qs@va", "-filter_hw_device", burnIn ? "va" : "qs");
            }
            else
            {
                Add(args, "-filter_hw_device", "va");
            }
        }
        else
        {
            foreach (var init in video.InitArgs)
            {
                args.Add(init);
            }
        }

        // Hardware-assisted decoding offloads the heavy decode of 4K/HEVC sources from the CPU. The GPU-resident
        // pipeline decodes on VAAPI and keeps frames in VRAM end to end. Otherwise QSV/VAAPI keep frames on
        // the GPU (an output format plus a leading hwdownload, below, brings them back for the software scale);
        // VideoToolbox auto-downloads. The caller forces software decode for the per-item retry after a failed
        // hardware decode, and for subtitle burn-in outside the GPU-resident pipeline (the overlay needs system
        // frames there).
        if (intelGpu)
        {
            Add(args, "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi");
        }
        else if (hwDecode)
        {
            args.Add("-hwaccel");
            args.Add(video.DecodeHwaccel!);
            if (!string.IsNullOrEmpty(video.DecodeOutputFormat))
            {
                args.Add("-hwaccel_output_format");
                args.Add(video.DecodeOutputFormat);
            }
        }

        var hasOffset = offset > TimeSpan.Zero;
        var seconds = offset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        // Always input-seek (before -i): ffmpeg jumps to the nearest keyframe without decoding, so the producer
        // writes its first frames immediately even when joining deep into a long item. Output seeking would
        // decode and discard from the start of the file -- minutes for a deep join into a 4K item, so the
        // producer writes nothing before it is given up on. For burn-in, -copyts preserves the source timestamps
        // so the libass subtitle overlay stays aligned to the seeked position (the timeline offset below removes
        // that base so the channel still starts at zero). -start_at_zero measures those timestamps from the file's
        // own start rather than its raw clock: a source whose start_time is not zero (a DVR .ts/.m2ts commonly
        // starts hundreds of seconds in) would otherwise land its output that far past the channel timeline, and
        // the extracted subtitle file (timed from the file's start) would no longer line up with the picture.
        if (hasOffset)
        {
            args.Add("-ss");
            args.Add(seconds);
            if (burnIn)
            {
                Add(args, "-copyts", "-start_at_zero");
            }
        }

        // Bound how much content is read (an input option, applied after the seek). The stress test sets this so N
        // copies of the exact production command race over a fixed slice; a time-of-day channel sets it only as a
        // defensive cap when its schedule hands over a slot shorter than the item. Millisecond precision, so a
        // short cap is not rounded away to nothing.
        if (durationLimit is { } limit && limit > TimeSpan.Zero)
        {
            args.Add("-t");
            args.Add(limit.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Read at exactly realtime so the segments are produced at the rate the player consumes them, keeping the
        // player a constant distance back from the live edge (see the pacing comment atop this class). The first
        // item of a session bursts the full head start before the player has tuned in; later items burst only the
        // session's accumulated wall-clock deficit (BurstForDeficit), which heals the time lost between processes
        // at item boundaries. A deficit-sized burst cannot lurch the live edge past schedule by construction, so
        // the player never falls off the back of the delete window.
        if (initialBurstSeconds > MinBurstSeconds)
        {
            Add(args, "-readrate", RealtimeReadRate, "-readrate_initial_burst", initialBurstSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            Add(args, "-readrate", RealtimeReadRate);
        }

        args.Add("-i");
        args.Add(path);

        // A video-only item still has to carry the channel's audio track: every item feeds one continuous stream,
        // and a stretch with no audio packets at all stalls players and breaks the concat path's same-streams
        // assumption. Silence is generated instead, exactly as long as the video this run plays, so the next item
        // (which chains from this output's end) starts with neither a hole nor an overlap in the audio. The length
        // is set explicitly because -shortest is not reliable for this: with an endless audio source it lets the
        // audio run seconds past the video, by a different amount every run. Under a burn-in seek's -copyts the
        // video keeps its source timestamps (starting at the seek point), so the silence is shifted to start there.
        var audioMap = AudioMap(audioOrdinal);
        if (silentAudio is { } silence && silence > TimeSpan.Zero)
        {
            if (burnIn && hasOffset)
            {
                Add(args, "-itsoffset", seconds);
            }

            Add(args, "-t", silence.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture), "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
            audioMap = "1:a:0";
        }

        // A linear channel mixes items of different resolutions, framerates, and aspect ratios. Players probe
        // the stream once and assume a fixed format, so the output stays constant: letterbox-pad every item to
        // the same WxH, force a constant framerate, and pin the pixel format (or upload it to the GPU).
        var height = OutputHeight(width);
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        string scale;

        // The GPU-resident graph's encoder hand-off, kept separate from the main chain so the burn-in variant
        // can splice its overlay between them. For a QSV encoder the frames hwmap from VAAPI into QSV; a VAAPI
        // encoder consumes the VAAPI frames directly.
        var gpuTail = string.Empty;
        if (intelGpu)
        {
            gpuTail = video.Name.Contains("qsv", StringComparison.Ordinal) ? ",hwmap=derive_device=qsv,format=qsv" : string.Empty;
            if (isHdr)
            {
                // Scale first so the tone map runs on the small frames, then PAD LAST: the letterbox bars must
                // be painted onto the SDR frame — painted before the tone map (in PQ/bt2020 space) the LUT turns
                // them green. procamp applies Jellyfin's VPP brightness/contrast gain BEFORE the tone map, the
                // same order Jellyfin uses: the gain acts on the PQ signal and the LUT re-normalises it into SDR,
                // compensating the LUT's dark rendering. Applied after the tone map instead, the same gain lifts
                // the finished SDR frame's black floor and washes the whole picture out.
                scale = "scale_vaapi=" + FitSize(width, height) + ":format=p010,"
                    + Procamp(video) + "tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709,"
                    + "pad_vaapi=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,fps=30";
            }
            else
            {
                // deinterlace_vaapi only touches flagged frames (like yadif's deint=1), so progressive content
                // passes through untouched; scale_vaapi converts 10-bit p010 surfaces to nv12 ON the GPU, so
                // 10-bit sources never need a software decode.
                scale = "deinterlace_vaapi=rate=frame:auto=1,"
                    + "scale_vaapi=" + FitSize(width, height) + ":format=nv12:out_color_matrix=bt709:out_range=tv,"
                    + "pad_vaapi=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,fps=30";
            }
        }
        else
        {
            // Deinterlace interlaced sources before scaling: it produces clean progressive frames and clears the
            // interlaced frame flag. deint=1 only touches flagged frames, so progressive content passes through
            // untouched. The encoder is also told the output is progressive via -field_order below. When the decoder
            // kept frames on the GPU, bring them back to system memory before the software scale.
            var download = hwDecode ? video.DecodeDownload : string.Empty;
            // Software tone map for HDR outside the GPU-resident pipeline (non-Intel encoders and the retry):
            // tonemapx when the caller found it (the SIMD tone mapper Jellyfin itself uses), else the zscale chain.
            // Decoded in software so the 10-bit depth survives. It runs AFTER the scale, on output-size frames: a
            // 4K source tone-mapped first costs four times the work for the same picture (measured 3.2x faster
            // with zscale, 5.2x with tonemapx, on a 4K HDR10 clip), and BEFORE the pad, so the bars are painted on
            // the finished SDR frame.
            var tonemap = isHdr ? (toneMapStage ?? ZscaleToneMap) + "," : string.Empty;
            // The scale sizes the picture from its display aspect ratio (see FitSize), and setsar=1 then marks the
            // now square pixels so mixed sources share one aspect ratio.
            // An SDR item is converted to the channel's colour format in the same scale pass (see ToChannelColour);
            // an HDR item is scaled as-is and the tone map produces bt709 limited itself.
            scale = download + "yadif=deint=1,scale=" + FitSize(width, height) + (isHdr ? string.Empty : ToChannelColour(inputMatrix)) + "," + tonemap
                + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30," + ChannelColourParams + "," + video.PixelStage;
        }

        if (burnIn && intelGpu)
        {
            AppendIntelGpuSubtitleFilter(args, path, forcedSubtitle!.Value, scale, gpuTail, externalSubtitlePath, audioMap, w, h, hasOffset ? seconds : "0", subtitleStyle, subtitleFontsDir);
        }
        else if (burnIn)
        {
            AppendSubtitleFilter(args, path, forcedSubtitle!.Value, scale, externalSubtitlePath, audioMap, subtitleStyle, subtitleFontsDir);
        }
        else
        {
            args.Add("-vf");
            args.Add(scale + gpuTail);

            // Map the first video stream and the audio track Jellyfin marks as default, so we play the same track
            // the user would hear in normal playback instead of letting ffmpeg pick by channel count. Any -map
            // disables ffmpeg's automatic stream selection, so the video must be mapped explicitly too.
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add(audioMap);
        }

        AppendEncoders(args, video, bitrate, audioEncoder, audioBitrate);

        // Continue the timeline from where the previous item ended so timestamps never jump backwards at a
        // boundary (backward jumps cause downstream non-monotonic DTS and audio desync). When the burn-in seek used
        // -copyts the source PTS starts at `offset`, so subtract it to land this item on the channel timeline.
        var tsOffset = timeline.TotalSeconds - (burnIn && hasOffset ? offset.TotalSeconds : 0);
        if (Math.Abs(tsOffset) > 0.0005)
        {
            args.Add("-output_ts_offset");
            args.Add(tsOffset.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Each item is a separate ffmpeg, so the muxer's continuity counters and timeline restart at every
        // boundary. Marking each output's first packets as a discontinuity tells the downstream reader to reset
        // its continuity/DTS expectations at the seam instead of flagging corrupt packets or non-monotonic DTS.
        Add(args, "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxpreload", "0", "-muxdelay", "0", "pipe:1");

        return args;
    }

    /// <summary>
    /// Builds the ffmpeg arguments for the whole channel as ONE continuous process using the concat demuxer:
    /// every item is decoded, scaled/padded to one frame, and re-encoded by a single encoder, so the output
    /// timestamps and MPEG-TS continuity counters never reset at an item boundary (no seams). The playlist is
    /// looped forever, and the caller rotates it to start at the item airing now, so the seek only ever lands
    /// inside the first listed file.
    /// </summary>
    /// <param name="listFilePath">Path to the concat demuxer list file (one <c>file '...'</c> line per item).</param>
    /// <param name="offset">Seek offset into the first listed item (how far into the airing item the clock is).</param>
    /// <param name="width">Target output width.</param>
    /// <param name="bitrate">Target video bitrate in kbps.</param>
    /// <param name="video">The resolved video-encoder profile. Its hardware decoder is used only when the caller
    /// passes <paramref name="decodeHwaccel"/> (a playlist whose items all share one resolution); the hardware
    /// encoder always applies.</param>
    /// <param name="audioEncoder">The ffmpeg audio encoder name.</param>
    /// <param name="audioBitrate">Target audio bitrate in kbps.</param>
    /// <param name="decodeHwaccel">The hardware decoder to use, or <c>null</c> to decode in software.</param>
    /// <param name="initialBurstSeconds">The tune-in head start: how much content is produced ahead of realtime before the player joins.</param>
    /// <param name="timelineBase">Where on the channel timeline this process's output starts, so a producer restarted mid-session continues forward instead of resetting timestamps to zero.</param>
    /// <param name="inputMatrix">The colour matrix every listed item's pixels are in (the concat path only takes items that share one), or <c>auto</c>.</param>
    /// <param name="audioOrdinal">The audio track to play, by position among each file's audio tracks (the concat path only takes items that play the same one).</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildConcat(
        string listFilePath,
        TimeSpan offset,
        int width,
        int bitrate,
        VideoEncoderProfile video,
        string audioEncoder,
        int audioBitrate,
        string? decodeHwaccel = null,
        double initialBurstSeconds = InitialBurstSeconds,
        TimeSpan timelineBase = default,
        string inputMatrix = "auto",
        int audioOrdinal = 0)
    {
        ArgumentNullException.ThrowIfNull(listFilePath);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        // -progress writes machine-readable key=value progress (including speed=) to stderr even at loglevel
        // error, so the Sessions tab can report the live encode speed without the noisy human stats line.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-progress", "pipe:2", "-fflags", "+genpts" };

        // Hardware encode device init (VAAPI/QSV) applies.
        foreach (var init in video.InitArgs)
        {
            args.Add(init);
        }

        // Hardware-decode the playlist when the caller allows it (every item is the same resolution, so the one
        // shared decoder never hits a mid-stream resolution change it cannot follow). QSV/VAAPI keep frames on
        // the GPU; the output format plus the leading hwdownload (below) bring them back for the software scale.
        var hwDecode = !string.IsNullOrEmpty(decodeHwaccel);
        if (hwDecode)
        {
            args.Add("-hwaccel");
            args.Add(decodeHwaccel!);
            if (!string.IsNullOrEmpty(video.DecodeOutputFormat))
            {
                args.Add("-hwaccel_output_format");
                args.Add(video.DecodeOutputFormat);
            }
        }

        if (offset > TimeSpan.Zero)
        {
            args.Add("-ss");
            args.Add(offset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Read at exactly realtime so segments are produced at the player's consumption rate. This is one
        // long-lived ffmpeg with no item boundaries, so a single up-front burst safely fills the tune-in head start
        // without ever lurching the live edge mid-stream. Loop the whole playlist forever and read it as one input.
        Add(args, "-readrate", RealtimeReadRate, "-readrate_initial_burst", initialBurstSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Add(args, "-stream_loop", "-1", "-safe", "0", "-f", "concat", "-i", listFilePath);

        var height = OutputHeight(width);
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        args.Add("-vf");
        // Deinterlace first (see Build): clears the interlaced flag so Jellyfin's re-transcode stays progressive
        // and does not add a deinterlace_vaapi pass that fails on QSV. The leading download (when present) brings
        // hardware-decoded frames back to system memory for the software scale.
        var download = hwDecode ? video.DecodeDownload : string.Empty;
        // The scale sizes the picture from its display aspect ratio (see FitSize), and setsar=1 then marks the
        // now square pixels so mixed sources share one aspect ratio.
        args.Add(download + "yadif=deint=1,scale=" + FitSize(width, height) + ToChannelColour(inputMatrix) + ","
            + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30," + ChannelColourParams + "," + video.PixelStage);

        // Map the video and the chosen audio track explicitly. Left to itself ffmpeg picks the audio track with the
        // most channels (the concat demuxer drops the files' default flags), which can be a 5.1 commentary or another
        // language rather than the track the channel chose and checked (ConcatCompatible).
        Add(args, "-map", "0:v:0", "-map", AudioMap(audioOrdinal));

        AppendEncoders(args, video, bitrate, audioEncoder, audioBitrate, ConcatAudioResyncSeconds);

        // One continuous encoder, so within a run no per-item timeline offset is needed. Across runs it matters:
        // a producer restarted mid-session (self-heal, or a session whose producer died under an active viewer)
        // must not rewind the timeline it appends to, so it starts where the previous output ended.
        if (timelineBase > TimeSpan.Zero)
        {
            args.Add("-output_ts_offset");
            args.Add(timelineBase.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Mark the first packets as a discontinuity: the self-heal loop restarts ffmpeg and appends to the same
        // stream the reader is following, so the fresh output's reset continuity counters/PCR must be flagged or
        // the reader sees a broken seam.
        // ffmpeg writes to stdout (pipe:1) and our process pumps that to the file. -y overwrites without prompting.
        Add(args, "-y", "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxpreload", "0", "-muxdelay", "0", "pipe:1");

        return args;
    }

    /// <summary>
    /// Builds the ffmpeg arguments for a standby "slate" clip used when a channel has no playable content, so
    /// viewers see an intentional standby card instead of a black screen or an error. When a font is available
    /// the card reads "Standby — channel content is unavailable"; otherwise it falls back to colour bars.
    /// </summary>
    /// <param name="width">Target output width.</param>
    /// <param name="bitrate">Target video bitrate in kbps.</param>
    /// <param name="seconds">Clip length; the caller loops it.</param>
    /// <param name="timeline">Output timestamp offset, continuing the channel's timeline.</param>
    /// <param name="fontPath">A TrueType font to label the slate, or <c>null</c> to fall back to colour bars.</param>
    /// <param name="video">The encoder profile, which must produce the same codec as the channel's content so the
    /// copy-mode segmenter never sees the codec change mid-playlist; <c>null</c> for software H.264.</param>
    /// <param name="audioEncoder">The ffmpeg audio encoder name, matching the channel's content.</param>
    /// <param name="audioBitrate">Target audio bitrate in kbps.</param>
    /// <param name="message">The line under "Standby" saying why (plain words, no quotes or colons), or <c>null</c> for the generic one.</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildSlate(int width, int bitrate, double seconds, TimeSpan timeline, string? fontPath, VideoEncoderProfile? video = null, string audioEncoder = "aac", int audioBitrate = 192, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(audioEncoder);
        video ??= new VideoEncoderProfile("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

        var height = OutputHeight(width);
        var size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        var hasFont = !string.IsNullOrEmpty(fontPath);

        // -progress reports the slate's output position like every producer, so a standby session's timeline keeps
        // advancing and a later restart resumes after the slate instead of rewinding over it.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-progress", "pipe:2" };
        foreach (var init in video.InitArgs)
        {
            args.Add(init);
        }

        // Pace the synthetic sources to realtime (-re) so the looped slate feeds the segmenter at the rate the
        // player consumes it. Without it ffmpeg renders each clip far faster than realtime and lurches the live
        // edge forward, the same failure a per-item burst would cause.
        Add(args, "-re", "-f", "lavfi", "-i", hasFont ? "color=c=0x0f1419:size=" + size + ":rate=30" : "smptebars=size=" + size + ":rate=30");
        Add(args, "-re", "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        Add(args, "-t", seconds.ToString("F3", CultureInfo.InvariantCulture));

        // The slate ends in the profile's own pixel stage, exactly as content does, so the encoder receives the
        // frame format (or GPU upload) it expects.
        if (hasFont)
        {
            // Escaped for the filtergraph like every other path, so a drive colon or an apostrophe cannot break it.
            var font = QuoteFilterValue(fontPath!);
            var title = (height / 10).ToString(CultureInfo.InvariantCulture);
            var sub = (height / 24).ToString(CultureInfo.InvariantCulture);
            Add(args, "-vf",
                "drawtext=fontfile=" + font + ":text='Standby':fontcolor=white:fontsize=" + title + ":x=(w-tw)/2:y=(h/2)-" + title + ","
                + "drawtext=fontfile=" + font + ":text='" + SlateText(message ?? "Channel content is unavailable") + "':fontcolor=0xaaaaaa:fontsize=" + sub + ":x=(w-tw)/2:y=(h/2)+10,"
                + ChannelColourParams + "," + video.PixelStage);
        }
        else
        {
            Add(args, "-vf", ChannelColourParams + "," + video.PixelStage);
        }

        AppendEncoders(args, video, bitrate, audioEncoder, audioBitrate);

        if (timeline > TimeSpan.Zero)
        {
            args.Add("-output_ts_offset");
            args.Add(timeline.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        Add(args, "-y", "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxpreload", "0", "-muxdelay", "0", "pipe:1");
        return args;
    }

    // Keeps a slate message to characters that need no escaping inside a single-quoted drawtext value: the
    // messages are the plugin's own constants, so this only guards against a future edit breaking the graph.
    private static string SlateText(string message)
        => new string(message.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-').ToArray());

    /// <summary>
    /// Converts a configured window length in minutes into the number of HLS segments to retain, clamped to a
    /// sensible floor. The window must cover the tune-in head start plus the worst-case burst advance of the
    /// self-heal path, and bounds disk use.
    /// </summary>
    /// <param name="windowMinutes">The desired on-disk window in minutes.</param>
    /// <returns>The segment count for the playlist window.</returns>
    public static int SegmentsForWindow(int windowMinutes) =>
        Math.Max(MinHlsSegments, Math.Max(1, windowMinutes) * 60 / HlsSegmentSeconds);

    /// <summary>
    /// Computes the catch-up burst for a per-item producer, anchored to the session's wall clock. The live edge
    /// SHOULD sit at <c>elapsed + InitialBurstSeconds</c> of content; the session has actually produced
    /// <paramref name="timeline"/>. The difference is wall-clock time lost between processes (cold starts,
    /// hardware-decode retries, subtitle extraction) that a realtime read rate can never recover, so each new
    /// producer bursts exactly that deficit. Clamped to zero (a producer ahead of schedule must not burst at all)
    /// and to the head start (one boundary never lurches the edge more than the original tune-in burst; repeated
    /// boundaries finish healing a larger backlog).
    /// </summary>
    /// <param name="elapsed">Wall-clock time since the per-item session started.</param>
    /// <param name="timeline">Content produced so far on the channel timeline (the next item's start position).</param>
    /// <param name="headStartSeconds">The session's tune-in head start, which is both the target lead and the cap.</param>
    /// <returns>The burst in seconds for the next producer, in [0, <paramref name="headStartSeconds"/>].</returns>
    public static double BurstForDeficit(TimeSpan elapsed, TimeSpan timeline, double headStartSeconds = InitialBurstSeconds)
        => Math.Clamp(elapsed.TotalSeconds + headStartSeconds - timeline.TotalSeconds, 0, headStartSeconds);

    /// <summary>
    /// Builds the ffmpeg arguments for the HLS segmenter: it reads the producer's continuous MPEG-TS on stdin and
    /// repackages it (no re-encode) into a rolling window of small segments plus a live playlist, deleting
    /// segments as they age out so on-disk size stays bounded no matter how long the channel runs.
    /// </summary>
    /// <param name="segmentPattern">The ffmpeg segment filename pattern (e.g. <c>.../seg%d.ts</c>).</param>
    /// <param name="playlistPath">The playlist (.m3u8) path Jellyfin reads.</param>
    /// <param name="listSize">How many segments to retain in the playlist window (see <see cref="SegmentsForWindow"/>).</param>
    /// <param name="startNumber">The first segment number to write. Non-zero when a producer is restarted into a directory that already holds segments, so the new segmenter appends past them instead of overwriting files a reader is still working through.</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildHlsSegmenter(string segmentPattern, string playlistPath, int listSize, long startNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(segmentPattern);
        ArgumentNullException.ThrowIfNull(playlistPath);

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        // Copy the incoming TS straight into HLS segments (no decode/encode), so this stage is nearly free.
        // delete_segments drops files once they leave the playlist window; omit_endlist keeps it a live playlist;
        // independent_segments marks each segment as starting on a keyframe. temp_file makes every playlist
        // rewrite atomic (write to .tmp, then rename): without it Jellyfin's reader polling at the live edge
        // eventually catches a truncated in-place write, fails the reload, and its HLS demuxer crashes (observed
        // as ffmpeg exit 139 killing playback). The window length (segments * time) is the player's buffer and
        // the bound on disk use.
        Add(args, "-fflags", "+genpts", "-i", "pipe:0", "-c", "copy",
            "-f", "hls",
            "-hls_time", HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-start_number", Math.Max(0, startNumber).ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", listSize.ToString(CultureInfo.InvariantCulture),
            "-hls_flags", "delete_segments+append_list+omit_endlist+independent_segments+temp_file",
            "-hls_segment_type", "mpegts",
            "-hls_segment_filename", segmentPattern,
            playlistPath);

        return args;
    }

    /// <summary>
    /// The output height for a 16:9 frame of the given width, rounded up to the even number every encoder needs.
    /// </summary>
    /// <param name="width">The output width.</param>
    /// <returns>The output height.</returns>
    internal static int OutputHeight(int width)
    {
        var height = (int)Math.Round(width * 9.0 / 16.0);
        return height % 2 != 0 ? height + 1 : height;
    }

    // Marks every frame with the channel's colour format right before the encoder (and before any GPU upload,
    // which carries the frame's properties to the hardware encoder), and clears the interlaced flag that yadif
    // leaves behind on progressive output.
    private const string ChannelColourParams = "setparams=field_mode=prog:range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709";

    /// <summary>
    /// The scale options that convert an SDR item into the channel's single colour format, bt709 matrix at
    /// limited range, in the same pass as the resize. A channel is one continuous stream whose colour description
    /// players read once, so every item has to arrive in the same format: an SD item's bt601 pixels shown as
    /// bt709 shift green and red by up to 39 of 255 (measured), and a full-range item (phone and screen
    /// recordings) shown as limited range clips its highlights and crushes its blacks. The input range comes from
    /// the decoded frames themselves; the input matrix is passed explicitly because an untagged frame would
    /// otherwise be read as bt601 whatever its size.
    /// </summary>
    /// <param name="inputMatrix">The item's colour matrix (see <see cref="InputMatrix"/>), or <c>auto</c>.</param>
    /// <returns>The options to append to a scale filter, starting with a colon.</returns>
    internal static string ToChannelColour(string inputMatrix)
        => ":in_color_matrix=" + (string.IsNullOrEmpty(inputMatrix) ? "auto" : inputMatrix) + ":out_color_matrix=bt709:out_range=tv";

    /// <summary>
    /// The colour matrix an item's pixels are in, as ffmpeg's scale names it: the item's own tag when it has one,
    /// otherwise what a player assumes for an untagged picture of its size (bt601 below 720 lines, bt709 from
    /// 720 up), and <c>auto</c> when nothing is known about the item at all.
    /// </summary>
    /// <param name="colorSpace">The item's tagged colour space as Jellyfin recorded it (e.g. <c>bt709</c>, <c>smpte170m</c>), or <c>null</c>.</param>
    /// <param name="sourceHeight">The item's picture height, or 0 when unknown.</param>
    /// <returns>The matrix name for <c>in_color_matrix</c>.</returns>
    public static string InputMatrix(string? colorSpace, int sourceHeight)
    {
        switch (colorSpace?.Trim().ToLowerInvariant())
        {
            case "bt709":
                return "bt709";
            case "smpte170m":
            case "bt470bg":
            case "bt601":
                return "bt601";
            case "smpte240m":
                return "smpte240m";
            case "fcc":
                return "fcc";
            case "bt2020nc":
            case "bt2020c":
            case "bt2020":
                return "bt2020";
            default:
                return sourceHeight <= 0 ? "auto" : sourceHeight < 720 ? "bt601" : "bt709";
        }
    }

    /// <summary>
    /// The <c>w</c>/<c>h</c> options that fit an item's picture inside the WxH frame at its true display aspect
    /// ratio, for <c>scale</c> and <c>scale_vaapi</c> alike (both evaluate them with ffmpeg's shared scale
    /// expression code, where an unsignalled SAR reads as square). The size comes from <c>dar</c> (stored size
    /// times the sample aspect ratio), not the stored pixels: <c>force_original_aspect_ratio</c> ignores the SAR, so
    /// an anamorphic 16:9 DVD (720x480 at 32:27) used to come out as a squeezed 3:2 picture. A picture within half a
    /// percent of the frame's own shape fills it exactly, so rounding never leaves a 2px sliver of padding, and both
    /// sides round to the even sizes every encoder needs.
    /// </summary>
    /// <param name="width">The output frame width.</param>
    /// <param name="height">The output frame height.</param>
    /// <returns>The scale options, e.g. <c>w='…':h='…'</c>.</returns>
    internal static string FitSize(int width, int height)
    {
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        var wide = "gte(dar," + (width / (double)height - 0.005).ToString("0.######", CultureInfo.InvariantCulture) + ")";
        return "w='if(" + wide + "," + w + ",min(" + w + ",trunc(" + h + "*dar/2+0.5)*2))'"
            + ":h='if(" + wide + ",min(" + h + ",trunc(" + w + "/dar/2+0.5)*2)," + h + ")'";
    }

    // The encoder half every producer shares (content, continuous playlist, and slate alike), so the segmenter
    // sees one uniform stream shape whichever process produced the bytes.
    private static void AppendEncoders(List<string> args, VideoEncoderProfile video, int bitrate, string audioEncoder, int audioBitrate, double audioResyncSeconds = AudioResyncSeconds)
    {
        var br = bitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:v", video.Name);

        // Force the encoder to signal progressive in the sequence header. yadif + setparams clear the per-frame
        // interlaced flag, but hardware encoders (notably h264_qsv) write the field order into the SPS from the
        // codec context at init and ignore the frame flag, so the output is tagged 1080i. Jellyfin then re-probes
        // our stream as interlaced and inserts a deinterlace_vaapi pass that fails on QSV. Setting the field order
        // at the encoder makes the output genuinely progressive, so Jellyfin remuxes it instead of re-transcoding.
        Add(args, "-field_order", "progressive");

        // Closed GOP gives the downstream segmenter predictable GOP boundaries, so Jellyfin can remux into HLS
        // instead of re-transcoding to realign keyframes.
        Add(args, "-flags", "+cgop");

        // Every item leaves in one colour format, bt709 limited range (see ToChannelColour), and the stream says
        // so explicitly: players and Jellyfin's remux read the colour description once, at tune-in, so an
        // untagged or per-item-tagged stream gets decoded with whatever they assumed for the first item.
        Add(args, "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv");

        if (video.UsePreset)
        {
            Add(args, "-preset", "veryfast", "-sc_threshold", "0");
        }

        foreach (var extra in video.ExtraEncoderArgs)
        {
            args.Add(extra);
        }

        // One GOP per HLS segment at the fixed frame rate, so the segmenter (and Jellyfin's delivery remux behind
        // it) cuts every segment at precisely SegmentSeconds: the first segment closes as early as possible at
        // tune-in and none is stretched waiting for a keyframe that lands past the mark.
        Add(args, "-b:v", br + "k", "-maxrate", br + "k",
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) + "k", "-g", GopFrames);

        // Re-sample audio to a constant 48 kHz stereo track and let aresample fill or drop samples to keep it
        // locked to the video, so audio never drifts out of sync within or across items. It resyncs once the
        // audio is audioResyncSeconds off its timestamps (see AudioResyncSeconds and ConcatAudioResyncSeconds).
        var abr = audioBitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:a", audioEncoder, "-b:a", abr + "k", "-ac", "2", "-ar", "48000",
            "-af", "aresample=async=1:min_hard_comp=" + audioResyncSeconds.ToString("0.000", CultureInfo.InvariantCulture));

        // A generous muxing queue absorbs the brief audio/video imbalance at each item's start instead of
        // aborting with "Too many packets buffered".
        Add(args, "-max_muxing_queue_size", "1024");
    }

    // Whether the encoder is an Intel hardware encoder (QSV or VAAPI), which can run the GPU-resident pipeline.
    private static bool IsIntelHardware(string encoderName)
        => encoderName.Contains("qsv", StringComparison.Ordinal) || encoderName.Contains("vaapi", StringComparison.Ordinal);

    // Appends a run of arguments (params avoids constant-array allocations the analyzer flags).
    private static void Add(List<string> args, params string[] values)
    {
        foreach (var value in values)
        {
            args.Add(value);
        }
    }

    // The audio stream specifier for the chosen default audio track, optional (`?`) so an item without that
    // track maps nothing instead of failing. ffmpeg's `a:N` indexes within audio streams, matching the ordinal
    // ChannelService returns; a missing ordinal falls back to the first audio track.
    private static string AudioMap(int? audioOrdinal)
        => "0:a:" + (audioOrdinal ?? 0).ToString(CultureInfo.InvariantCulture) + "?";

    // Burns the chosen forced subtitle into the picture. Text subtitles render through the libass-backed
    // `subtitles` filter; bitmap subtitles (PGS/VOBSUB) are composited with `overlay`.
    private static void AppendSubtitleFilter(List<string> args, string path, (int RelativeIndex, bool IsText) forced, string scale, string? externalSubtitlePath, string audioMap, string? style = null, string? fontsDir = null)
    {
        var index = forced.RelativeIndex.ToString(CultureInfo.InvariantCulture);
        var options = SubtitleOptions(style, fontsDir);
        string filter;
        if (!forced.IsText)
        {
            // Bitmap subtitle (PGS/VOBSUB): composite the mapped, already-seeked subtitle stream. It is a picture,
            // so neither the style override nor a font directory applies.
            filter = "[0:v][0:s:" + index + "]overlay," + scale + "[v]";
        }
        else if (!string.IsNullOrEmpty(externalSubtitlePath))
        {
            // Jellyfin's own extracted subtitle file (the same one its transcodes burn): a tiny file libass reads
            // instantly, instead of scanning the whole media file to reach the seek point.
            filter = "[0:v]subtitles=filename=" + QuoteFilterValue(externalSubtitlePath) + options + "," + scale + "[v]";
        }
        else
        {
            // Text subtitle read straight from the media file (the fallback while Jellyfin's extraction warms).
            filter = "[0:v]subtitles=filename=" + QuoteFilterValue(path) + ":si=" + index + options + "," + scale + "[v]";
        }

        args.Add("-filter_complex");
        args.Add(filter);
        args.Add("-map");
        args.Add("[v]");
        args.Add("-map");
        args.Add(audioMap);
    }

    // Burns the chosen subtitle INSIDE the GPU-resident Intel pipeline: the video never leaves VRAM. Only the
    // subtitle picture renders on the CPU -- text via alphasrc+libass at 10 fps (a transparent canvas the
    // subtitles filter draws onto), bitmaps as sparse sub2video frames -- and is uploaded once for a GPU
    // overlay_vaapi composite. This mirrors Jellyfin's own Intel burn-in graphs, so a Forced-only channel
    // playing foreign-language 4K keeps the full GPU speed instead of dropping to the software chain.
    private static void AppendIntelGpuSubtitleFilter(List<string> args, string path, (int RelativeIndex, bool IsText) forced, string mainChain, string gpuTail, string? externalSubtitlePath, string audioMap, string w, string h, string startSeconds, string? style = null, string? fontsDir = null)
    {
        var index = forced.RelativeIndex.ToString(CultureInfo.InvariantCulture);
        var options = SubtitleOptions(style, fontsDir);
        string subChain;
        if (!forced.IsText)
        {
            // Bitmap subtitle (PGS/VOBSUB): scale the sparse sub2video frames to the output size, then upload.
            subChain = "[0:s:" + index + "]scale=" + w + ":" + h + "[subsw];[subsw]hwupload[sub]";
        }
        else
        {
            // Text subtitle: libass draws onto a transparent canvas; `start=` aligns the canvas timestamps with
            // the -copyts seek so the overlay stays in sync on a deep tune-in.
            var source = QuoteFilterValue(!string.IsNullOrEmpty(externalSubtitlePath) ? externalSubtitlePath : path);
            var si = string.IsNullOrEmpty(externalSubtitlePath) ? ":si=" + index : string.Empty;
            subChain = "alphasrc=s=" + w + "x" + h + ":r=10:start=" + startSeconds + "[bg];"
                + "[bg]subtitles=filename=" + source + si + options + ":alpha=1[subsw];[subsw]hwupload[sub]";
        }

        // eof_action=pass keeps the video flowing after the subtitle track runs dry (it usually ends early).
        var tailPart = gpuTail.Length > 0 ? "[ov];[ov]" + gpuTail.TrimStart(',') + "[v]" : "[v]";
        var filter = "[0:v]" + mainChain + "[main];" + subChain + ";[main][sub]overlay_vaapi=eof_action=pass" + tailPart;
        args.Add("-filter_complex");
        args.Add(filter);
        args.Add("-map");
        args.Add("[v]");
        args.Add("-map");
        args.Add(audioMap);
    }

    // The brightness/contrast gain stage ahead of a VAAPI tone map (tonemap_vaapi has no brightness knob of
    // its own and Intel's LUT renders dark; boosting the PQ input, as Jellyfin does, brightens the result
    // without lifting the SDR black floor). Values come from Jellyfin's VPP tone-mapping settings via the
    // encoder profile; neutral values (b=0, c=1) add no stage. Returns "procamp_vaapi=...," or "" for splicing.
    private static string Procamp(VideoEncoderProfile video)
    {
        var hasBrightness = Math.Abs(video.VppBrightness) > 0.0005;
        var hasContrast = Math.Abs(video.VppContrast - 1) > 0.0005;
        if (!hasBrightness && !hasContrast)
        {
            return string.Empty;
        }

        var stage = "procamp_vaapi=b=" + video.VppBrightness.ToString("0.###", CultureInfo.InvariantCulture);
        if (hasContrast)
        {
            stage += ":c=" + video.VppContrast.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return stage + ",";
    }

    // The trailing options every libass `subtitles` invocation shares: the appearance override and the directory
    // of fonts attached to the media. Both values are single-quoted, so the commas inside a style override are
    // not read as the end of the filter, and both are omitted entirely when unset, leaving the subtitle rendered
    // exactly as authored (inline bold/italic/colour tags included, since those are per-event overrides that win
    // over any style).
    private static string SubtitleOptions(string? style, string? fontsDir)
    {
        var options = string.Empty;
        if (!string.IsNullOrEmpty(fontsDir))
        {
            options += ":fontsdir=" + QuoteFilterValue(fontsDir);
        }

        if (!string.IsNullOrEmpty(style))
        {
            options += ":force_style='" + style + "'";
        }

        return options;
    }

    /// <summary>
    /// Quotes a value (a file path) for use as a filter option inside a <c>-filter_complex</c> graph. ffmpeg
    /// unescapes it twice: the filtergraph parser first strips one level of quoting (where <c>, ; [ ]</c> would
    /// otherwise end the filter or start a pad label), then the filter's own option parser strips a second
    /// (where <c>:</c> would start the next option). So the value is escaped for the option parser (backslash
    /// before <c>\ ' :</c>) and the result single-quoted for the graph parser. A quote cannot appear inside a
    /// quoted span, so each one closes the span, adds an escaped quote, and reopens it: <c>'\''</c>.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The value, escaped and quoted, ready to follow <c>option=</c>.</returns>
    internal static string QuoteFilterValue(string value)
    {
        var option = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);
        return "'" + option.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
