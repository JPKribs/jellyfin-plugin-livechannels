namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The video codec family a channel is encoded to. The concrete encoder (software or a hardware one) is then
/// chosen from Jellyfin's own hardware-acceleration configuration.
/// </summary>
public enum VideoCodec
{
    /// <summary>H.264 / AVC. Universally compatible; the safe default.</summary>
    H264 = 0,

    /// <summary>H.265 / HEVC. Smaller files at the same quality, but fewer clients can play it.</summary>
    Hevc = 1
}
