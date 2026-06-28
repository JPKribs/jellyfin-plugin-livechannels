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
        bool isInterlaced = false,
        int? audioOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        var burnIn = forcedSubtitle.HasValue;

        // The all-GPU VAAPI pipeline (Intel/AMD), built the way Tunarr/ErsatzTV build it: decode, deinterlace,
        // tone-map, scale and pad all run on VAAPI surfaces and feed the VAAPI encoder directly, so frames never
        // return to system memory. It handles any bit depth and HDR without a hwdownload, which is exactly the
        // step that crashed the old path on 10-bit (p010) sources. Subtitle burn-in (the overlay needs system
        // frames) and the software-decode fallback drop to the software filter chain below.
        var vaapiPipeline = video.VaapiFilters && !burnIn && !softwareDecode;
        var hwDecode = !vaapiPipeline && !softwareDecode && !string.IsNullOrEmpty(video.DecodeHwaccel);

        // +genpts fills in any missing presentation timestamps so each item starts from a clean, monotonic
        // timeline before the offset is applied.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-fflags", "+genpts" };

        // Hardware device initialisation goes before the input.
        foreach (var init in video.InitArgs)
        {
            args.Add(init);
        }

        // Hardware-assisted decoding offloads the heavy decode of 4K/HEVC sources from the CPU. The VAAPI pipeline
        // decodes on the GPU and keeps frames there through every filter. VideoToolbox auto-downloads. The caller
        // can force software decode (the per-item fallback when a hardware decode fails, and for subtitle burn-in
        // whose overlay needs system frames).
        if (vaapiPipeline)
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

        // Pace the read to realtime so the producer doesn't race ahead of the player (wasting CPU encoding far
        // in advance and bloating the temp file). The initial burst lets it fill the head-start buffer fast on
        // tune-in before settling to 1x, the same pacing ErsatzTV/Tunarr use.
        Add(args, "-readrate", "1.0", "-readrate_initial_burst", "15");

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
        if (vaapiPipeline)
        {
            scale = VaapiFilterChain(w, h, isHdr, isInterlaced);
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
        string? decodeHwaccel = null,
        string outputTarget = "pipe:1")
    {
        ArgumentNullException.ThrowIfNull(listFilePath);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-fflags", "+genpts" };

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

        // Pace the read to realtime (with an initial burst to fill the tune-in buffer) so the one long-lived
        // ffmpeg doesn't race ahead of the player, the same as ErsatzTV/Tunarr. Loop the whole playlist forever
        // and read it as one continuous input.
        Add(args, "-readrate", "1.0", "-readrate_initial_burst", "15");
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
        // discontinuity: on the file path the self-heal loop restarts ffmpeg and appends to the same file Jellyfin
        // keeps reading, so the fresh stream's reset continuity counters/PCR must be flagged or the reader sees a
        // broken seam. The target is stdout (pipe:1) when our process pumps the output to a file, or a named-pipe
        // path when ffmpeg writes the pipe directly. -y overwrites the path target without prompting.
        Add(args, "-y", "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxpreload", "0", "-muxdelay", "0", outputTarget);

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
    public static List<string> BuildSlate(int width, int bitrate, double seconds, TimeSpan timeline, string? fontPath, string outputTarget = "pipe:1")
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
        Add(args, "-f", "lavfi", "-i", hasFont ? "color=c=0x0f1419:size=" + size + ":rate=30" : "smptebars=size=" + size + ":rate=30");
        Add(args, "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
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

        Add(args, "-y", "-mpegts_flags", "+initial_discontinuity", "-f", "mpegts", "-muxdelay", "0", outputTarget);
        return args;
    }

    // The all-GPU VAAPI filter chain, mirroring Tunarr/ErsatzTV's Intel pipeline. Every step runs on a VAAPI
    // surface and the result feeds the VAAPI encoder directly, so frames never touch system memory:
    //   deinterlace_vaapi  - only for interlaced sources; progressive content skips it (deinterlacing progressive
    //                        frames would halve their quality), so unlike the auto yadif=deint=1 this is gated.
    //   tonemap_vaapi      - HDR (PQ/HLG) to SDR bt709, on the GPU, outputting nv12.
    //   scale_vaapi        - scale to fit within WxH preserving aspect (force_original_aspect_ratio=decrease) and
    //                        pin the output to 8-bit nv12, which also converts a 10-bit (p010) surface on the GPU.
    //   pad_vaapi          - letterbox to exactly WxH, auto-centred (x=-1:y=-1).
    //   fps                - constant frame rate so every item shares one stream shape (runs fine on VAAPI frames).
    private static string VaapiFilterChain(string w, string h, bool isHdr, bool isInterlaced)
    {
        var chain = string.Empty;
        if (isInterlaced)
        {
            chain += "deinterlace_vaapi,";
        }

        if (isHdr)
        {
            chain += "tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709,";
        }

        return chain
            + "scale_vaapi=w=" + w + ":h=" + h + ":format=nv12:extra_hw_frames=64:force_divisible_by=2:force_original_aspect_ratio=decrease,setsar=1,"
            + "pad_vaapi=w=" + w + ":h=" + h + ":x=-1:y=-1:color=black,fps=30";
    }

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
