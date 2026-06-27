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
/// <param name="DecodeHwaccel">The ffmpeg <c>-hwaccel</c> decoder to assist decoding (e.g. <c>videotoolbox</c>), or <c>null</c> for software decode. Frames are auto-downloaded to system memory for the software scale stage.</param>
public sealed record VideoEncoderProfile(
    string Name,
    string DisplayName,
    bool IsHardware,
    IReadOnlyList<string> InitArgs,
    IReadOnlyList<string> ExtraEncoderArgs,
    string PixelStage,
    bool UsePreset,
    string? DecodeHwaccel = null);
