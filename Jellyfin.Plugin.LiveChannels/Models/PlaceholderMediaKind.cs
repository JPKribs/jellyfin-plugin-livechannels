namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// What a custom placeholder's file is, read from its own leading bytes.
/// </summary>
public enum PlaceholderMediaKind
{
    /// <summary>A still PNG, JPEG or WebP image.</summary>
    Image = 0,

    /// <summary>A GIF, still or animated.</summary>
    Gif = 1,

    /// <summary>A video in an MP4/MOV, Matroska/WebM, AVI or MPEG-TS container.</summary>
    Video = 2
}
