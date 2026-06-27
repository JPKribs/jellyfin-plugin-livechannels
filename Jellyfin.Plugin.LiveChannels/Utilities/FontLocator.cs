using System.IO;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Finds a TrueType font that ffmpeg's <c>drawtext</c> filter can use, trying common locations across macOS,
/// Linux (including Jellyfin's Docker images), and Windows. Used to label the standby slate and generated
/// channel logos; callers fall back gracefully when no font is found.
/// </summary>
public static class FontLocator
{
    private static readonly string[] Candidates =
    {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "C:\\Windows\\Fonts\\arial.ttf"
    };

    /// <summary>
    /// Returns the first available font path, or <c>null</c> when none of the known locations exist.
    /// </summary>
    /// <returns>A usable font path, or <c>null</c>.</returns>
    public static string? Find()
    {
        foreach (var candidate in Candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
