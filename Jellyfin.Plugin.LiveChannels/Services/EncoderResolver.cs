using System;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

// Encoder-specific argument runs, hoisted to static fields (the analyzer rejects inline constant arrays).

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Resolves which ffmpeg encoders a channel stream should use. The video encoder follows Jellyfin's own
/// hardware-acceleration configuration (Dashboard &gt; Playback): if Jellyfin is set up to encode with
/// VideoToolbox, NVENC, QSV, VAAPI, or AMF, the matching hardware encoder is used for the chosen codec;
/// otherwise it falls back to software (libx264 / libx265).
/// </summary>
public class EncoderResolver
{
    private static readonly string[] Empty = Array.Empty<string>();
    private static readonly string[] VideotoolboxExtra = { "-allow_sw", "1" };
    private static readonly string[] KnownAccels = { "videotoolbox", "nvenc", "amf", "qsv", "vaapi" };

    private readonly IServerConfigurationManager _config;
    private readonly ILogger<EncoderResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncoderResolver"/> class.
    /// </summary>
    /// <param name="config">The server configuration manager, used to read Jellyfin's encoding options.</param>
    /// <param name="logger">The logger.</param>
    public EncoderResolver(IServerConfigurationManager config, ILogger<EncoderResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the video encoder profile for a codec, honouring Jellyfin's hardware acceleration.
    /// </summary>
    /// <param name="codec">The target codec family.</param>
    /// <param name="allowHardware">Whether hardware encoding may be used (subtitle burn-in forces software).</param>
    /// <returns>The resolved encoder profile.</returns>
    public VideoEncoderProfile ResolveVideo(VideoCodec codec, bool allowHardware)
    {
        var options = ReadOptions();
        // The plugin's own "disable hardware acceleration" switch forces software end to end (encode and
        // decode), the one path guaranteed to work on any system, codec, and media type.
        var disabled = Plugin.Instance?.ReadConfiguration(c => c.DisableHardwareAcceleration) ?? false;
        var accel = allowHardware && !disabled ? Detect(options) : "none";
        var family = codec == VideoCodec.Hevc ? "hevc" : "h264";
        var label = codec == VideoCodec.Hevc ? "HEVC" : "H.264";

        switch (accel)
        {
            // Hardware ENCODE follows the server's configured accelerator for every vendor. Hardware DECODE
            // offloads the heaviest part of the pipeline (decoding 4K/HEVC sources) from the CPU. Intel (QSV/VAAPI)
            // runs the all-GPU VAAPI pipeline below, keeping frames on the GPU through every filter. VideoToolbox
            // hardware-decodes and auto-downloads to system memory for the software scale. The per-item path falls
            // back to software decode if a hardware decode fails, so a codec a driver cannot decode never breaks
            // playback. NVENC/AMF stay encode-only (no decode offload wired up here).
            case "videotoolbox":
                return new VideoEncoderProfile(family + "_videotoolbox", label + " (VideoToolbox)", true,
                    Empty, VideotoolboxExtra, "format=yuv420p", false, DecodeHwaccel: "videotoolbox");
            case "nvenc":
                return new VideoEncoderProfile(family + "_nvenc", label + " (NVENC)", true,
                    Empty, Empty, "format=yuv420p", false);
            case "amf":
                return new VideoEncoderProfile(family + "_amf", label + " (AMF)", true,
                    Empty, Empty, "format=yuv420p", false);
            // Intel QSV: decode and filter on VAAPI (scale/pad/deinterlace/tone-map on the GPU), then hwmap the
            // frames onto a derived QSV device and encode with h264_qsv. This mirrors Jellyfin's own working Intel
            // command exactly (vaapi=va,driver=iHD -> qsv=qs@va -> filter_hw_device qs -> ... -> h264_qsv), so it
            // runs on the hardware/driver Jellyfin already proves works, and frames never leave the GPU (no
            // hwdownload-to-nv12, the step that crashed 10-bit sources). The filter chain appends the hwmap in
            // StreamArguments when the encoder is QSV. PixelStage uploads to QSV for the software fallback.
            case "qsv":
                var qsvDevice = ResolveVaapiDevice(options);
                _logger.LogInformation("Live Channels: QSV-over-VAAPI pipeline on {Device}", qsvDevice);
                return new VideoEncoderProfile(family + "_qsv", label + " (QSV)", true,
                    new[]
                    {
                        "-init_hw_device", "vaapi=va:" + qsvDevice + ",driver=iHD",
                        "-init_hw_device", "qsv=qs@va",
                        "-filter_hw_device", "qs"
                    },
                    Empty, "format=nv12,hwupload=extra_hw_frames=64", false,
                    DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: string.Empty, VaapiFilters: true);

            // Plain VAAPI (e.g. AMD): decode, filter and encode all on VAAPI, no QSV hop. Frames stay on the GPU.
            case "vaapi":
                var vaapiDevice = ResolveVaapiDevice(options);
                _logger.LogInformation("Live Channels: VAAPI pipeline on {Device}", vaapiDevice);
                return new VideoEncoderProfile(family + "_vaapi", label + " (VAAPI)", true,
                    new[] { "-init_hw_device", "vaapi=va:" + vaapiDevice, "-filter_hw_device", "va" }, Empty,
                    "format=nv12,hwupload", false,
                    DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: string.Empty, VaapiFilters: true);
            default:
                return Software(codec);
        }
    }

    /// <summary>
    /// Describes the hardware acceleration that will be used, for display in the settings UI.
    /// </summary>
    /// <returns>A friendly label and whether it is hardware-accelerated.</returns>
    public (string Label, bool IsHardware) DescribeAcceleration()
    {
        if (Plugin.Instance?.ReadConfiguration(c => c.DisableHardwareAcceleration) ?? false)
        {
            return ("Software (hardware acceleration disabled below)", false);
        }

        return Detect(ReadOptions()) switch
        {
            "videotoolbox" => ("VideoToolbox", true),
            "nvenc" => ("NVENC", true),
            "amf" => ("AMF", true),
            "qsv" => ("Intel (VAAPI)", true),
            "vaapi" => ("VAAPI", true),
            _ => ("Software (no hardware acceleration configured in Jellyfin)", false)
        };
    }

    // The DRM render node the VAAPI pipeline binds to, taken straight from Jellyfin's encoding config so we land
    // on the exact GPU its own hardware transcoding uses. On Linux QSV/VAAPI, VaapiDevice is the canonical render
    // node (the QSV device is derived from it and is usually left blank), so it wins. Only when nothing is
    // configured do we fall back: the single render node present, or renderD128 as a last resort.
    private string ResolveVaapiDevice(EncodingOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.VaapiDevice))
        {
            return options!.VaapiDevice;
        }

        if (!string.IsNullOrWhiteSpace(options?.QsvDevice))
        {
            return options!.QsvDevice;
        }

        try
        {
            var nodes = System.IO.Directory.GetFiles("/dev/dri", "renderD*");
            if (nodes.Length > 0)
            {
                Array.Sort(nodes, StringComparer.Ordinal);
                _logger.LogInformation("Live Channels: no VAAPI device configured in Jellyfin; using detected node {Node}", nodes[0]);
                return nodes[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate /dev/dri render nodes");
        }

        return "/dev/dri/renderD128";
    }

    private EncodingOptions? ReadOptions()
    {
        try
        {
            return _config.GetEncodingOptions();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Jellyfin encoding options; using software encoding");
            return null;
        }
    }

    // Returns a canonical acceleration token ("videotoolbox", "nvenc", ...) or "none". Matching on the string
    // form keeps this working whether HardwareAccelerationType is an enum or a string across Jellyfin versions.
    private static string Detect(EncodingOptions? options)
    {
        if (options is null || !options.EnableHardwareEncoding)
        {
            return "none";
        }

        var name = (options.HardwareAccelerationType.ToString() ?? "none").ToLowerInvariant();
        foreach (var known in KnownAccels)
        {
            if (name.Contains(known, StringComparison.Ordinal))
            {
                return known;
            }
        }

        return "none";
    }

    /// <summary>
    /// Resolves the ffmpeg audio encoder name and a sensible bitrate (kbps) for the chosen codec.
    /// </summary>
    /// <param name="codec">The target audio codec.</param>
    /// <returns>The encoder name and bitrate in kbps.</returns>
    public static (string Encoder, int BitrateKbps) ResolveAudio(AudioCodec codec)
        => codec switch
        {
            AudioCodec.Ac3 => ("ac3", 256),
            AudioCodec.Eac3 => ("eac3", 256),
            _ => ("aac", 192)
        };

    private static VideoEncoderProfile Software(VideoCodec codec)
        => codec == VideoCodec.Hevc
            ? new VideoEncoderProfile("libx265", "HEVC (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true)
            : new VideoEncoderProfile("libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);
}
