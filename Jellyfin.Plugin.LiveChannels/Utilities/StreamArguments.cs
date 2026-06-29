using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Builds the ffmpeg argument list for a single item in a channel's stream. Pure (no Jellyfin dependencies)
/// so the transcode/burn-in argument shapes can be unit-tested directly.
/// </summary>
public static class StreamArguments
{
    // The producer reads just above realtime. Its output is packaged by an HLS segmenter into a rolling window of
    // small segments (the playlist), and that window is the buffer the player draws from. A small margin over
    // realtime keeps the producer from ever being the bottleneck: it can recover the head start that a per-item
    // boundary gap consumes instead of sitting exactly at the live edge. It must stay close to realtime, though,
    // because reading much faster pushes the window forward faster than the player consumes it and the player
    // falls off the back as old segments are deleted. The burst is applied ONCE per session (the first item, or
    // the single concat ffmpeg) to fill a head start before tune-in; it must never apply to a later item mid-stream.
    private const string ReadRate = "1.1";
    private const string ReadRateInitialBurst = "30";

    // The HLS segmenter packages the producer's continuous TS into a rolling, self-trimming playlist of fixed
    // length segments. How many segments are retained (the window) is configurable; see SegmentsForWindow. The
    // window has to cover the drift the 1.1x ReadRate creates (the live edge advances ~0.1s per second faster than
    // the player consumes), and it is also the upper bound on disk use, so it trades catch-up headroom for space.
    private const int HlsSegmentSeconds = 4;

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
        bool initialBurst = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        var burnIn = forcedSubtitle.HasValue;

        // HDR on an Intel hardware encoder (QSV/VAAPI) tone-maps entirely on the iGPU: VAAPI decode, tonemap_vaapi,
        // scale and pad on VAAPI, then map to QSV for the encoder. Software tone-mapping a 4K HDR source cannot
        // hold realtime on low-power hardware (e.g. an N100), so this is the only way HDR plays there. Burn-in and
        // the software-decode fallback drop back to the software tone-map path below.
        var useHdrVaapi = isHdr && !burnIn && !softwareDecode && IsIntelHardware(video.Name);
        var hwDecode = !useHdrVaapi && !softwareDecode && !string.IsNullOrEmpty(video.DecodeHwaccel);

        // +genpts fills in any missing presentation timestamps so each item starts from a clean, monotonic
        // timeline before the offset is applied.
        // -progress writes machine-readable key=value progress (including speed=) to stderr even at loglevel
        // error, so the Sessions tab can report the live encode speed without the noisy human stats line.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-progress", "pipe:2", "-fflags", "+genpts" };

        // Hardware device initialisation goes before the input. The HDR path needs a VAAPI device with a QSV
        // device derived from it (so VAAPI filters and the QSV encoder share frames); everything else uses the
        // encoder profile's own init.
        foreach (var init in useHdrVaapi ? HdrVaapiInit : video.InitArgs)
        {
            args.Add(init);
        }

        // Hardware-assisted decoding offloads the heavy decode of 4K/HEVC sources from the CPU. The HDR path
        // decodes on VAAPI and keeps frames on the GPU through the tone-map. Otherwise QSV/VAAPI keep frames on
        // the GPU (an output format plus a leading hwdownload, below, brings them back for the software scale);
        // VideoToolbox auto-downloads. The caller can force software decode (the per-item fallback when a hardware
        // decode fails, and for subtitle burn-in whose overlay needs system frames the simple way).
        if (useHdrVaapi)
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
        // that base so the channel still starts at zero).
        if (hasOffset)
        {
            args.Add("-ss");
            args.Add(seconds);
            if (burnIn)
            {
                args.Add("-copyts");
            }
        }

        // Read at realtime so the segments are produced at the rate the player consumes them, keeping the player a
        // constant distance back from the live edge (see ReadRate). Only the first item of a session bursts a head
        // start, and only before the player has tuned in: a burst on a later item would shove its content into the
        // playlist faster than realtime, lurching the live edge forward until the player falls off the back of the
        // delete window and the stream breaks.
        if (initialBurst)
        {
            Add(args, "-readrate", ReadRate, "-readrate_initial_burst", ReadRateInitialBurst);
        }
        else
        {
            Add(args, "-readrate", ReadRate);
        }

        args.Add("-i");
        args.Add(path);

        // A linear channel mixes items of different resolutions, framerates, and aspect ratios. Players probe
        // the stream once and assume a fixed format, so the output stays constant: letterbox-pad every item to
        // the same WxH, force a constant framerate, and pin the pixel format (or upload it to the GPU).
        var height = (int)Math.Round(width * 9.0 / 16.0);
        if (height % 2 != 0)
        {
            height++;
        }

        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        string scale;
        if (useHdrVaapi)
        {
            // Full GPU HDR pipeline: tone-map PQ/HLG to SDR bt709, scale and letterbox on VAAPI, then map onto a
            // QSV frame for the encoder. fps pins the constant frame rate; pad_vaapi keeps a 1:1 aspect.
            scale = "tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709,"
                + "scale_vaapi=w=" + w + ":h=" + h + ":force_original_aspect_ratio=decrease,"
                + "pad_vaapi=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,fps=30,hwmap=derive_device=qsv,format=qsv";
        }
        else
        {
            // Deinterlace interlaced sources (e.g. broadcast/DVD music videos) before scaling: it produces clean
            // progressive frames and clears the interlaced frame flag. deint=1 only touches flagged frames, so
            // progressive content passes through untouched. The encoder is also told the output is progressive
            // via -field_order below; the frame flag alone is not enough on every encoder (see that line). When
            // the decoder kept frames on the GPU, bring them back to system memory before the software scale.
            var download = hwDecode ? video.DecodeDownload : string.Empty;
            // Software tone-map fallback for HDR off the VAAPI path (non-Intel encoders, burn-in, the retry): the
            // zscale->tonemap(hable)->zscale chain (ErsatzTV/Tunarr). Decoded in software so the 10-bit survives.
            var tonemap = isHdr
                ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,"
                : string.Empty;
            // setsar=1 normalises non-square (anamorphic) pixels so mixed sources share one aspect ratio.
            scale = download + "yadif=deint=1," + tonemap + "scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,"
                + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,setparams=field_mode=prog," + video.PixelStage;
        }

        if (burnIn)
        {
            AppendSubtitleFilter(args, path, forcedSubtitle!.Value, scale, externalSubtitlePath, audioOrdinal);
        }
        else
        {
            args.Add("-vf");
            args.Add(scale);

            // Map the first video stream and the audio track Jellyfin marks as default, so we play the same track
            // the user would hear in normal playback instead of letting ffmpeg pick by channel count. Any -map
            // disables ffmpeg's automatic stream selection, so the video must be mapped explicitly too.
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add(AudioMap(audioOrdinal));
        }

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

        if (video.UsePreset)
        {
            Add(args, "-preset", "veryfast", "-sc_threshold", "0");
        }

        foreach (var extra in video.ExtraEncoderArgs)
        {
            args.Add(extra);
        }

        Add(args, "-b:v", br + "k", "-maxrate", br + "k",
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) + "k", "-g", "50");

        // Re-sample audio to a constant 48 kHz stereo track and let aresample fill or drop samples to keep it
        // locked to the video, so audio never drifts out of sync within or across items.
        var abr = audioBitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:a", audioEncoder, "-b:a", abr + "k", "-ac", "2", "-ar", "48000", "-af", "aresample=async=1:min_hard_comp=0.100");

        // A generous muxing queue absorbs the brief audio/video imbalance at each item's start instead of
        // aborting with "Too many packets buffered".
        Add(args, "-max_muxing_queue_size", "1024");

        // Continue the timeline from where the previous item ended so timestamps never jump backwards at a
        // boundary (the cause of downstream non-monotonic DTS and audio desync). When the burn-in seek used
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
    /// looped forever and the stream is seeked to the current position on tune-in.
    /// </summary>
    /// <param name="listFilePath">Path to the concat demuxer list file (one <c>file '...'</c> line per item).</param>
    /// <param name="offset">Seek offset into the concatenated loop (the current wall-clock position).</param>
    /// <param name="width">Target output width.</param>
    /// <param name="bitrate">Target video bitrate in kbps.</param>
    /// <param name="video">The resolved video-encoder profile. Its hardware *decoder* is not used here, because
    /// hardware decoders fail on the per-segment resolution changes a concatenated playlist produces; the
    /// hardware *encoder* still applies.</param>
    /// <param name="audioEncoder">The ffmpeg audio encoder name.</param>
    /// <param name="audioBitrate">Target audio bitrate in kbps.</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildConcat(
        string listFilePath,
        TimeSpan offset,
        int width,
        int bitrate,
        VideoEncoderProfile video,
        string audioEncoder,
        int audioBitrate,
        string? decodeHwaccel = null)
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

        // Read at realtime (see ReadRate) so segments are produced at the player's consumption rate. This is one
        // long-lived ffmpeg with no item boundaries, so a single up-front burst safely fills the tune-in head start
        // without ever lurching the live edge mid-stream. Loop the whole playlist forever and read it as one input.
        Add(args, "-readrate", ReadRate, "-readrate_initial_burst", ReadRateInitialBurst);
        Add(args, "-stream_loop", "-1", "-safe", "0", "-f", "concat", "-i", listFilePath);

        var height = (int)Math.Round(width * 9.0 / 16.0);
        if (height % 2 != 0)
        {
            height++;
        }

        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        args.Add("-vf");
        // Deinterlace first (see Build): clears the interlaced flag so Jellyfin's re-transcode stays progressive
        // and does not add a deinterlace_vaapi pass that fails on QSV. The leading download (when present) brings
        // hardware-decoded frames back to system memory for the software scale.
        var download = hwDecode ? video.DecodeDownload : string.Empty;
        // setsar=1 normalises non-square (anamorphic) pixels so mixed sources share one aspect ratio.
        args.Add(download + "yadif=deint=1,scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,"
            + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,setparams=field_mode=prog," + video.PixelStage);

        var br = bitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:v", video.Name);

        // Signal progressive in the sequence header so Jellyfin remuxes instead of re-transcoding (see Build).
        Add(args, "-field_order", "progressive");

        // Closed GOP for clean segment boundaries (see Build).
        Add(args, "-flags", "+cgop");

        if (video.UsePreset)
        {
            Add(args, "-preset", "veryfast", "-sc_threshold", "0");
        }

        foreach (var extra in video.ExtraEncoderArgs)
        {
            args.Add(extra);
        }

        Add(args, "-b:v", br + "k", "-maxrate", br + "k",
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) + "k", "-g", "50");

        var abr = audioBitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:a", audioEncoder, "-b:a", abr + "k", "-ac", "2", "-ar", "48000", "-af", "aresample=async=1:min_hard_comp=0.100");
        Add(args, "-max_muxing_queue_size", "1024");

        // One continuous encoder, so no per-item timeline offset is needed. But mark the first packets as a
        // discontinuity: the self-heal loop restarts ffmpeg and appends to the same file Jellyfin keeps reading,
        // so the fresh stream's reset continuity counters/PCR must be flagged or the reader sees a broken seam.
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
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildSlate(int width, int bitrate, double seconds, TimeSpan timeline, string? fontPath)
    {
        var height = (int)Math.Round(width * 9.0 / 16.0);
        if (height % 2 != 0)
        {
            height++;
        }

        var size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        var br = bitrate.ToString(CultureInfo.InvariantCulture);
        var hasFont = !string.IsNullOrEmpty(fontPath);

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        // Pace the synthetic sources to realtime (-re) so the looped slate feeds the segmenter at the rate the
        // player consumes it. Without it ffmpeg renders each clip far faster than realtime and lurches the live
        // edge forward, the same failure a per-item burst would cause.
        Add(args, "-re", "-f", "lavfi", "-i", hasFont ? "color=c=0x0f1419:size=" + size + ":rate=30" : "smptebars=size=" + size + ":rate=30");
        Add(args, "-re", "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        Add(args, "-t", seconds.ToString("F0", CultureInfo.InvariantCulture));

        if (hasFont)
        {
            // Forward slashes work on every OS in ffmpeg paths and avoid backslash-escaping headaches.
            var font = fontPath!.Replace("\\", "/", StringComparison.Ordinal);
            var title = (height / 10).ToString(CultureInfo.InvariantCulture);
            var sub = (height / 24).ToString(CultureInfo.InvariantCulture);
            Add(args, "-vf",
                "drawtext=fontfile='" + font + "':text='Standby':fontcolor=white:fontsize=" + title + ":x=(w-tw)/2:y=(h/2)-" + title + ","
                + "drawtext=fontfile='" + font + "':text='Channel content is unavailable':fontcolor=0xaaaaaa:fontsize=" + sub + ":x=(w-tw)/2:y=(h/2)+10,"
                + "format=yuv420p");
        }
        else
        {
            Add(args, "-vf", "format=yuv420p");
        }

        Add(args, "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-b:v", br + "k", "-g", "50");
        Add(args, "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000");

        if (timeline > TimeSpan.Zero)
        {
            args.Add("-output_ts_offset");
            args.Add(timeline.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        Add(args, "-y", "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxdelay", "0", "pipe:1");
        return args;
    }

    /// <summary>
    /// Converts a configured window length in minutes into the number of HLS segments to retain, clamped to a
    /// sensible floor. The window must cover the drift the above-realtime producer creates, and bounds disk use.
    /// </summary>
    /// <param name="windowMinutes">The desired on-disk window in minutes.</param>
    /// <returns>The segment count for the playlist window.</returns>
    public static int SegmentsForWindow(int windowMinutes) =>
        Math.Max(MinHlsSegments, Math.Max(1, windowMinutes) * 60 / HlsSegmentSeconds);

    /// <summary>
    /// Builds the ffmpeg arguments for the HLS segmenter: it reads the producer's continuous MPEG-TS on stdin and
    /// repackages it (no re-encode) into a rolling window of small segments plus a live playlist, deleting
    /// segments as they age out so on-disk size stays bounded no matter how long the channel runs.
    /// </summary>
    /// <param name="segmentPattern">The ffmpeg segment filename pattern (e.g. <c>.../seg%d.ts</c>).</param>
    /// <param name="playlistPath">The playlist (.m3u8) path Jellyfin reads.</param>
    /// <param name="listSize">How many segments to retain in the playlist window (see <see cref="SegmentsForWindow"/>).</param>
    /// <returns>The ffmpeg argument list.</returns>
    public static List<string> BuildHlsSegmenter(string segmentPattern, string playlistPath, int listSize)
    {
        ArgumentNullException.ThrowIfNull(segmentPattern);
        ArgumentNullException.ThrowIfNull(playlistPath);

        var args = new List<string> { "-hide_banner", "-loglevel", "error" };

        // Copy the incoming TS straight into HLS segments (no decode/encode), so this stage is nearly free.
        // delete_segments drops files once they leave the playlist window; omit_endlist keeps it a live playlist;
        // independent_segments marks each segment as starting on a keyframe. The window length (segments * time)
        // is the player's buffer and also the upper bound on disk use.
        Add(args, "-fflags", "+genpts", "-i", "pipe:0", "-c", "copy",
            "-f", "hls",
            "-hls_time", HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_list_size", listSize.ToString(CultureInfo.InvariantCulture),
            "-hls_flags", "delete_segments+append_list+omit_endlist+independent_segments",
            "-hls_segment_type", "mpegts",
            "-hls_segment_filename", segmentPattern,
            playlistPath);

        return args;
    }

    // Device init for the HDR path: a VAAPI device (Intel iHD) with a QSV device derived from it, so the VAAPI
    // tone-map/scale filters and the QSV encoder share GPU frames through a hwmap.
    private static readonly string[] HdrVaapiInit =
    {
        "-init_hw_device", "vaapi=va:,vendor_id=0x8086,driver=iHD",
        "-init_hw_device", "qsv=qs@va",
        "-filter_hw_device", "qs"
    };

    // Whether the encoder is an Intel hardware encoder (QSV or VAAPI), which can run the GPU HDR tone-map path.
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
    private static void AppendSubtitleFilter(List<string> args, string path, (int RelativeIndex, bool IsText) forced, string scale, string? externalSubtitlePath, int? audioOrdinal)
    {
        var index = forced.RelativeIndex.ToString(CultureInfo.InvariantCulture);
        string filter;
        if (!forced.IsText)
        {
            // Bitmap subtitle (PGS/VOBSUB): composite the mapped, already-seeked subtitle stream.
            filter = "[0:v][0:s:" + index + "]overlay," + scale + "[v]";
        }
        else if (!string.IsNullOrEmpty(externalSubtitlePath))
        {
            // A pre-extracted subtitle file (used on a deep tune-in): a tiny file libass reads instantly,
            // instead of scanning the whole media file to reach the seek point.
            filter = "[0:v]subtitles=filename='" + EscapeSubtitlePath(externalSubtitlePath) + "'," + scale + "[v]";
        }
        else
        {
            // Text subtitle read straight from the media file (full items playing from the start).
            filter = "[0:v]subtitles=filename='" + EscapeSubtitlePath(path) + "':si=" + index + "," + scale + "[v]";
        }

        args.Add("-filter_complex");
        args.Add(filter);
        args.Add("-map");
        args.Add("[v]");
        args.Add("-map");
        args.Add(AudioMap(audioOrdinal));
    }

    // libavfilter parses the subtitles filename: backslash, single quote, and colon must be escaped so the
    // path is not split on the option separator or treated as a quote boundary.
    private static string EscapeSubtitlePath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);
}
