using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
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
        => ResolveVideoCore(codec, allowHardware) with { Deinterlacer = DeinterlacerOf(ReadOptions()) };

    // ResolveVideo before Jellyfin's deinterlacing method is applied (it is used wherever frames are deinterlaced in
    // software, the hardware profiles' software-decode fallback included).
    private VideoEncoderProfile ResolveVideoCore(VideoCodec codec, bool allowHardware)
    {
        var options = ReadOptions();
        // The plugin's own "disable hardware acceleration" switch forces software end to end (encode and
        // decode), the one path guaranteed to work on any system, codec, and media type.
        var disabled = Plugin.Instance?.ReadConfiguration(c => c.DisableHardwareAcceleration) ?? false;
        var accel = allowHardware && !disabled ? Detect(options) : "none";
        var family = codec == VideoCodec.Hevc ? "hevc" : "h264";
        var label = codec == VideoCodec.Hevc ? "HEVC" : "H.264";

        // Detect never returns an accelerator without options, so past this guard options is non-null and the
        // hardware branches can read it directly (a null-conditional there would be a provably constant check).
        if (options is null)
        {
            return Software(codec, null);
        }

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
                    ? FirstDevice(options.QsvDevice, options.VaapiDevice)
                    : null;
                // With a known render node, derive the QSV device from it explicitly; the bare "qsv=hw" form
                // lets ffmpeg auto-pick the default node (renderD128), which is wrong on multi-GPU boxes.
                var qsvInit = qsvDevice is null
                    ? QsvInit
                    : new[] { "-init_hw_device", "vaapi=va:" + qsvDevice, "-init_hw_device", "qsv=hw@va", "-filter_hw_device", "hw" };
                // "-hwaccel qsv" does not make ffmpeg's own decoders decode on the GPU (that takes an explicit
                // *_qsv decoder), so it decoded in software and the hwdownload below then failed every item before
                // a software retry. With a render node the GPU-resident pipeline decodes on VAAPI, so this profile's
                // separate decode does the same; without one (Windows) it decodes in software from the start.
                return new VideoEncoderProfile(family + "_qsv", label + " (QSV)", true,
                    qsvInit, LowPower(options, codec), "format=nv12,hwupload=extra_hw_frames=64", false,
                    DecodeHwaccel: qsvDevice is null ? null : "vaapi", DecodeOutputFormat: qsvDevice is null ? null : "vaapi", DecodeDownload: qsvDevice is null ? string.Empty : "hwdownload,format=nv12|p010le,",
                    GpuDevice: qsvDevice, VppBrightness: VppBrightness(options), VppContrast: VppContrast(options));
            case "vaapi":
                var device = FirstDevice(options.VaapiDevice, options.QsvDevice);
                return new VideoEncoderProfile(family + "_vaapi", label + " (VAAPI)", true,
                    new[] { "-init_hw_device", "vaapi=va:" + device, "-filter_hw_device", "va" }, LowPower(options, codec),
                    "format=nv12,hwupload", false,
                    DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: "hwdownload,format=nv12|p010le,",
                    GpuDevice: device, VppBrightness: VppBrightness(options), VppContrast: VppContrast(options));
            default:
                return Software(codec, options);
        }
    }

    /// <summary>
    /// Whether Jellyfin's hardware decoding settings (Dashboard &gt; Playback &gt; Transcoding) allow decoding an item on
    /// the GPU. The plugin decodes on the GPU exactly when Jellyfin itself would, so a format the server's GPU
    /// cannot decode (one its owner left unticked) goes straight to the software path instead of failing on the
    /// GPU first. An item whose format was not recorded (a schedule cached before it was) keeps the GPU.
    /// </summary>
    /// <param name="program">The item.</param>
    /// <returns>Whether the item may decode on the GPU.</returns>
    public bool CanHardwareDecode(ProgramEntry program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var options = ReadOptions();
        return options is null || CanHardwareDecode(options, program.SourceVideoCodec, program.SourceBitDepth ?? (program.IsHdr ? 10 : null), program.SourcePixelFormat);
    }

    /// <summary>
    /// The decision behind <see cref="CanHardwareDecode(ProgramEntry)"/>: the codec must be ticked under "Enable
    /// hardware decoding for", a 10-bit HEVC or VP9 source needs its 10-bit switch, and an HEVC range extension
    /// source (4:2:2 or 4:4:4) needs the 10-bit or 12-bit HEVC RExt switch for its depth. A 12-bit 4:2:0 HEVC source
    /// counts as range extension too, as it is in the HEVC profiles.
    /// </summary>
    /// <param name="options">Jellyfin's encoding options.</param>
    /// <param name="codec">The source codec as ffprobe names it (<c>h264</c>, <c>hevc</c>, ...), or <c>null</c> when unknown.</param>
    /// <param name="bitDepth">The source bit depth, or <c>null</c> when unknown (taken as 8).</param>
    /// <param name="pixelFormat">The source pixel format, or <c>null</c> when unknown.</param>
    /// <returns>Whether the source may decode on the GPU.</returns>
    internal static bool CanHardwareDecode(EncodingOptions options, string? codec, int? bitDepth, string? pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(codec))
        {
            return true;
        }

        var allowed = options.HardwareDecodingCodecs ?? Array.Empty<string>();
        if (!allowed.Any(c => string.Equals(c, codec, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var depth = bitDepth ?? 8;
        var chroma422Or444 = pixelFormat is { } format
            && (format.Contains("422", StringComparison.Ordinal) || format.Contains("444", StringComparison.Ordinal));
        if (string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            if (chroma422Or444 || depth > 10)
            {
                return depth > 10 ? options.EnableDecodingColorDepth12HevcRext : options.EnableDecodingColorDepth10HevcRext;
            }

            return depth <= 8 || options.EnableDecodingColorDepth10Hevc;
        }

        if (string.Equals(codec, "vp9", StringComparison.OrdinalIgnoreCase))
        {
            return depth <= 8 || options.EnableDecodingColorDepth10Vp9;
        }

        return true;
    }

    /// <summary>
    /// The software tone-map stage for HDR items, built from the server's own tone-mapping settings (Dashboard >
    /// Playback > Transcoding) so a channel renders HDR exactly as Jellyfin's own playback does. Only for an ffmpeg
    /// that has jellyfin-ffmpeg's tonemapx; the caller checks that.
    /// </summary>
    /// <returns>The tonemapx filter, ending in 8-bit bt709 4:2:0.</returns>
    public string SoftwareToneMap()
    {
        var options = ReadOptions();
        return ToneMapX(options?.TonemappingAlgorithm ?? TonemappingAlgorithm.bt2390, options?.TonemappingDesat ?? 0, options?.TonemappingPeak ?? 100, options?.TonemappingParam ?? 0);
    }

    /// <summary>
    /// Builds a tonemapx stage. A "none" algorithm would pass the PQ signal through as if it were SDR (a washed
    /// out picture), so it falls back to bt2390, Jellyfin's default; an unset peak falls back to 100 nits.
    /// </summary>
    /// <param name="algorithm">The tone-mapping curve.</param>
    /// <param name="desat">The highlight desaturation strength.</param>
    /// <param name="peak">The signal peak override in nits, or 0 for the default.</param>
    /// <param name="param">The curve's tuning parameter, or 0 for its default.</param>
    /// <returns>The tonemapx filter.</returns>
    internal static string ToneMapX(TonemappingAlgorithm algorithm, double desat, double peak, double param)
    {
        var curve = algorithm == TonemappingAlgorithm.none ? TonemappingAlgorithm.bt2390 : algorithm;
        var stage = "tonemapx=tonemap=" + curve.ToString().ToLowerInvariant()
            + ":desat=" + Math.Max(0, desat).ToString("0.###", CultureInfo.InvariantCulture)
            + ":peak=" + (peak > 0 ? peak : 100).ToString("0.###", CultureInfo.InvariantCulture);
        if (param > 0)
        {
            stage += ":param=" + param.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return stage + ":t=bt709:m=bt709:p=bt709:r=tv:format=yuv420p";
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

    // The software encoder, tuned by Jellyfin's own transcoding settings: its encoding preset (Auto keeps veryfast,
    // which holds realtime on modest CPUs), its thread count (Auto leaves it to ffmpeg) and its deinterlacer.
    private static VideoEncoderProfile Software(VideoCodec codec, EncodingOptions? options)
    {
        var threads = options is { EncodingThreadCount: > 0 } ? new[] { "-threads", options.EncodingThreadCount.ToString(CultureInfo.InvariantCulture) } : Array.Empty<string>();
        var profile = codec == VideoCodec.Hevc
            ? new VideoEncoderProfile("libx265", "HEVC (software)", false, Array.Empty<string>(), threads, "format=yuv420p", true)
            : new VideoEncoderProfile("libx264", "H.264 (software)", false, Array.Empty<string>(), threads, "format=yuv420p", true);
        return profile with { Preset = PresetOf(options), Deinterlacer = DeinterlacerOf(options) };
    }

    /// <summary>The software encoder preset Jellyfin's settings name: its "Encoding preset", or <c>veryfast</c> on Auto.</summary>
    /// <param name="options">Jellyfin's encoding options, or <c>null</c>.</param>
    /// <returns>The preset.</returns>
    internal static string PresetOf(EncodingOptions? options)
        => options is null || options.EncoderPreset == EncoderPreset.auto ? "veryfast" : options.EncoderPreset.ToString();

    /// <summary>The software deinterlacer Jellyfin's settings name: its "Deinterlacing method".</summary>
    /// <param name="options">Jellyfin's encoding options, or <c>null</c>.</param>
    /// <returns>The filter name (<c>yadif</c> or <c>bwdif</c>).</returns>
    internal static string DeinterlacerOf(EncodingOptions? options)
        => options?.DeinterlaceMethod == DeinterlaceMethod.bwdif ? "bwdif" : "yadif";

    // Intel's low-power (fixed-function) encoder, when Jellyfin's "Enable Intel Low-Power" switch for the codec is on:
    // it encodes on dedicated hardware, leaving the GPU's shader units free for decoding and tone mapping.
    private static string[] LowPower(EncodingOptions options, VideoCodec codec)
        => (codec == VideoCodec.Hevc ? options.EnableIntelLowPowerHevcHwEncoder : options.EnableIntelLowPowerH264HwEncoder)
            ? new[] { "-low_power", "1" }
            : Array.Empty<string>();
}
