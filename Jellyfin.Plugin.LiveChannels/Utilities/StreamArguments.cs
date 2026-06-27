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
        string? externalSubtitlePath = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(audioEncoder);

        var burnIn = forcedSubtitle.HasValue;

        // +genpts fills in any missing presentation timestamps so each item starts from a clean, monotonic
        // timeline before the offset is applied.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-fflags", "+genpts" };

        // Hardware device initialisation (VAAPI/QSV) goes before the input.
        foreach (var init in video.InitArgs)
        {
            args.Add(init);
        }

        // Hardware-assisted decoding (no output format set, so frames land in system memory for the software
        // scale/pad). Offloads the heavy decode of 4K/HEVC sources from the CPU.
        if (!string.IsNullOrEmpty(video.DecodeHwaccel))
        {
            args.Add("-hwaccel");
            args.Add(video.DecodeHwaccel);
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
        // Deinterlace interlaced sources (e.g. broadcast/DVD music videos) before scaling: it produces clean
        // progressive frames and clears the interlaced frame flag. deint=1 only touches flagged frames, so
        // progressive content passes through untouched. The encoder is also told the output is progressive via
        // -field_order below; the frame flag alone is not enough on every encoder (see that line).
        var scale = "yadif=deint=1,scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,"
            + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,fps=30,setparams=field_mode=prog," + video.PixelStage;

        if (burnIn)
        {
            AppendSubtitleFilter(args, path, forcedSubtitle!.Value, scale, externalSubtitlePath);
        }
        else
        {
            args.Add("-vf");
            args.Add(scale);
        }

        var br = bitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:v", video.Name);

        // Force the encoder to signal progressive in the sequence header. yadif + setparams clear the per-frame
        // interlaced flag, but hardware encoders (notably h264_qsv) write the field order into the SPS from the
        // codec context at init and ignore the frame flag, so the output is tagged 1080i. Jellyfin then re-probes
        // our stream as interlaced and inserts a deinterlace_vaapi pass that fails on QSV. Setting the field order
        // at the encoder makes the output genuinely progressive, so Jellyfin remuxes it instead of re-transcoding.
        Add(args, "-field_order", "progressive");

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
        // shared decoder never hits a mid-stream resolution change it cannot follow). Frames land in system
        // memory for the software scale, the same as the per-item path.
        if (!string.IsNullOrEmpty(decodeHwaccel))
        {
            args.Add("-hwaccel");
            args.Add(decodeHwaccel);
        }

        if (offset > TimeSpan.Zero)
        {
            args.Add("-ss");
            args.Add(offset.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        // Loop the whole playlist forever and read it as one continuous input.
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
        // and does not add a deinterlace_vaapi pass that fails on QSV.
        args.Add("yadif=deint=1,scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,"
            + "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,fps=30,setparams=field_mode=prog," + video.PixelStage);

        var br = bitrate.ToString(CultureInfo.InvariantCulture);
        Add(args, "-c:v", video.Name);

        // Signal progressive in the sequence header so Jellyfin remuxes instead of re-transcoding (see Build).
        Add(args, "-field_order", "progressive");

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

        // One continuous encoder, so no per-item timeline offset and no discontinuity flag are needed. The
        // target is stdout (pipe:1) when our process pumps the output to a file, or a named-pipe path when
        // ffmpeg writes the pipe directly (so killing ffmpeg, not our process, cleanly ends a stuck write).
        // -y overwrites the path target without prompting (the named pipe already exists from mkfifo).
        Add(args, "-y", "-f", "mpegts", "-muxpreload", "0", "-muxdelay", "0", outputTarget);

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

    // Appends a run of arguments (params avoids constant-array allocations the analyzer flags).
    private static void Add(List<string> args, params string[] values)
    {
        foreach (var value in values)
        {
            args.Add(value);
        }
    }

    // Burns the chosen forced subtitle into the picture. Text subtitles render through the libass-backed
    // `subtitles` filter; bitmap subtitles (PGS/VOBSUB) are composited with `overlay`.
    private static void AppendSubtitleFilter(List<string> args, string path, (int RelativeIndex, bool IsText) forced, string scale, string? externalSubtitlePath)
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
        args.Add("0:a?");
    }

    // libavfilter parses the subtitles filename: backslash, single quote, and colon must be escaped so the
    // path is not split on the option separator or treated as a quote boundary.
    private static string EscapeSubtitlePath(string path)
        => path
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal);
}
