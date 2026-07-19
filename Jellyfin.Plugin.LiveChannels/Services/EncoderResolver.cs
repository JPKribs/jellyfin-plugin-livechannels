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
    private static readonly string[] QsvInit = { "-init_hw_device", "qsv=hw", "-filter_hw_device", "hw" };
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
            // offloads the heaviest part of the pipeline (decoding 4K/HEVC sources) from the CPU. VideoToolbox
            // auto-downloads decoded frames to system memory; QSV/VAAPI keep frames on the GPU, so they set an
            // output format and a leading hwdownload that brings frames back for the software scale. The download
            // must allow nv12 (8-bit) AND p010le (10-bit): a 10-bit source (most HEVC, all 4K HDR) decodes to a
            // p010 surface, and pinning the download to nv12 alone makes it fail, dropping the whole item to a
            // software decode that cannot hold realtime (the choppiness on HEVC/4K). The continuous and per-item
            // paths still fall back to software decode if a hardware decode genuinely fails, so a codec a driver
            // cannot decode never breaks playback. NVENC/AMF stay encode-only (no decode offload wired up here).
            case "videotoolbox":
                return new VideoEncoderProfile(family + "_videotoolbox", label + " (VideoToolbox)", true,
                    Empty, VideotoolboxExtra, "format=yuv420p", false, DecodeHwaccel: "videotoolbox");
            case "nvenc":
                return new VideoEncoderProfile(family + "_nvenc", label + " (NVENC)", true,
                    Empty, Empty, "format=yuv420p", false);
            case "amf":
                return new VideoEncoderProfile(family + "_amf", label + " (AMF)", true,
                    Empty, Empty, "format=yuv420p", false);
            case "qsv":
                // On Linux, QSV sits on a VAAPI render node: expose Jellyfin's configured device so the per-item
                // pipeline can run fully GPU-resident (VAAPI decode/filters, QSV encode via hwmap). Jellyfin's
                // dashboard stores the node in QsvDevice when QSV is the selected accelerator, not VaapiDevice.
                var qsvDevice = OperatingSystem.IsLinux()
                    ? FirstDevice(options?.QsvDevice, options?.VaapiDevice)
                    : null;
                // With a known render node, derive the QSV device from it explicitly; the bare "qsv=hw" form
                // lets ffmpeg auto-pick the default node (renderD128), which is wrong on multi-GPU boxes.
                var qsvInit = qsvDevice is null
                    ? QsvInit
                    : new[] { "-init_hw_device", "vaapi=va:" + qsvDevice, "-init_hw_device", "qsv=hw@va", "-filter_hw_device", "hw" };
                return new VideoEncoderProfile(family + "_qsv", label + " (QSV)", true,
                    qsvInit, Empty, "format=nv12,hwupload=extra_hw_frames=64", false,
                    DecodeHwaccel: "qsv", DecodeOutputFormat: "qsv", DecodeDownload: "hwdownload,format=nv12|p010le,",
                    GpuDevice: qsvDevice, VppBrightness: VppBrightness(options), VppContrast: VppContrast(options));
            case "vaapi":
                var device = FirstDevice(options?.VaapiDevice, options?.QsvDevice);
                return new VideoEncoderProfile(family + "_vaapi", label + " (VAAPI)", true,
                    new[] { "-init_hw_device", "vaapi=va:" + device, "-filter_hw_device", "va" }, Empty,
                    "format=nv12,hwupload", false,
                    DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: "hwdownload,format=nv12|p010le,",
                    GpuDevice: device, VppBrightness: VppBrightness(options), VppContrast: VppContrast(options));
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
            "qsv" => ("QSV (Intel Quick Sync)", true),
            "vaapi" => ("VAAPI", true),
            _ => ("Software (no hardware acceleration configured in Jellyfin)", false)
        };
    }

    // The render node Jellyfin is configured to encode on. QSV mode stores it in QsvDevice and VAAPI mode
    // in VaapiDevice; the caller passes the active mode's field first, and the first node is only assumed
    // when neither is set.
    private static string FirstDevice(string? preferred, string? fallback)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            return preferred;
        }

        return string.IsNullOrEmpty(fallback) ? "/dev/dri/renderD128" : fallback;
    }

    // The brightness/contrast gain Jellyfin applies after its own VAAPI tone map (Dashboard > Playback >
    // "VPP Tone mapping brightness gain"). Intel's fixed-function HDR->SDR LUT renders dark, so the channel
    // streams honour the same setting the user tuned for regular playback. Clamped to procamp_vaapi's ranges.
    private static double VppBrightness(EncodingOptions? options)
        => Math.Clamp(options?.VppTonemappingBrightness ?? 0, -100, 100);

    private static double VppContrast(EncodingOptions? options)
        => Math.Clamp(options?.VppTonemappingContrast ?? 1, 0, 10);

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
