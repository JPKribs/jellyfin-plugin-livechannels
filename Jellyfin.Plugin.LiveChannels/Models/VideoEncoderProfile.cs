using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A resolved ffmpeg video-encoder recipe: the encoder name plus the surrounding arguments it needs (hardware
/// device init, an extra filter stage to move frames onto the GPU, and encoder-specific flags).
/// </summary>
/// <param name="Name">The ffmpeg <c>-c:v</c> encoder, e.g. <c>libx264</c> or <c>h264_videotoolbox</c>.</param>
/// <param name="DisplayName">A friendly label for the UI, e.g. "H.264 (VideoToolbox)".</param>
/// <param name="IsHardware">Whether this is a hardware-accelerated encoder.</param>
/// <param name="InitArgs">Arguments placed before the input (hardware device initialisation), or empty.</param>
/// <param name="ExtraEncoderArgs">Encoder-specific arguments (e.g. videotoolbox's <c>-allow_sw</c>), or empty.</param>
/// <param name="PixelStage">The trailing filter stage that pins the pixel format / uploads to the GPU.</param>
/// <param name="UsePreset">Whether to add the libx264/libx265 <c>-preset</c> and scene-cut flags.</param>
/// <param name="DecodeHwaccel">The ffmpeg <c>-hwaccel</c> decoder to assist decoding (e.g. <c>videotoolbox</c>, <c>qsv</c>), or <c>null</c> for software decode.</param>
/// <param name="DecodeOutputFormat">The <c>-hwaccel_output_format</c> for the decoder (e.g. <c>qsv</c>), or <c>null</c>. Decoders that keep frames on the GPU set this and a matching <see cref="DecodeDownload"/>; VideoToolbox auto-downloads and leaves both unset.</param>
/// <param name="DecodeDownload">The leading filter that brings hardware-decoded frames back to system memory for the software scale stage (e.g. <c>hwdownload,format=nv12,</c>), or empty when the decoder already delivers system frames.</param>
/// <param name="VaapiFilters">Whether this profile uses the all-GPU VAAPI filter chain (<c>scale_vaapi</c>/<c>pad_vaapi</c>/<c>deinterlace_vaapi</c>/<c>tonemap_vaapi</c>) instead of the download-to-software-scale-reupload path. Frames stay on the GPU end to end, so no bit depth or HDR ever needs a hwdownload (the source of the exit-234 crashes). Mirrors how Tunarr/ErsatzTV build the Intel pipeline.</param>
public sealed record VideoEncoderProfile(
    string Name,
    string DisplayName,
    bool IsHardware,
    IReadOnlyList<string> InitArgs,
    IReadOnlyList<string> ExtraEncoderArgs,
    string PixelStage,
    bool UsePreset,
    string? DecodeHwaccel = null,
    string? DecodeOutputFormat = null,
    string DecodeDownload = "",
    bool VaapiFilters = false);
