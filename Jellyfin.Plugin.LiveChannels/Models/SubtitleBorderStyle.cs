namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>How burned-in subtitle text is separated from the picture behind it.</summary>
public enum SubtitleBorderStyle
{
    /// <summary>Leave the subtitle file's own border style alone.</summary>
    Default = 0,

    /// <summary>Draw an outline (and drop shadow) around the glyphs.</summary>
    Outline = 1,

    /// <summary>Draw a solid box behind the text.</summary>
    Box = 2
}
