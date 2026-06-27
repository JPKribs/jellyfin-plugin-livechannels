namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The audio codec a channel's stream is encoded to.
/// </summary>
public enum AudioCodec
{
    /// <summary>AAC. Universal stereo audio; the safe default.</summary>
    Aac = 0,

    /// <summary>AC3 (Dolby Digital). Widely supported by TVs and receivers.</summary>
    Ac3 = 1,

    /// <summary>E-AC3 (Dolby Digital Plus). Higher quality where clients support it.</summary>
    Eac3 = 2
}
