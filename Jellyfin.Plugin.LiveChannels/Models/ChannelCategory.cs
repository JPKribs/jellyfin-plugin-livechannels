namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A channel-wide guide category tag. Kids is set per rating block, and the movie tag is applied automatically
/// when the airing item is a movie, so those are not options here.
/// </summary>
public enum ChannelCategory
{
    /// <summary>No channel-wide category tag.</summary>
    None = 0,

    /// <summary>Tag the channel as news.</summary>
    News = 1,

    /// <summary>Tag the channel as sports.</summary>
    Sports = 2
}
