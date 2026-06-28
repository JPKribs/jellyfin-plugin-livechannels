namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The content type a channel weights more heavily in its loop.
/// </summary>
public enum FavorKind
{
    /// <summary>No type is favoured; the loop is proportional to the library.</summary>
    None = 0,

    /// <summary>Favour movies.</summary>
    Movies = 1,

    /// <summary>Favour episodes (shows).</summary>
    Shows = 2,

    /// <summary>Favour music videos.</summary>
    MusicVideos = 3
}
