namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// How a <see cref="LibrarySource"/> narrows the content pulled from its library. Exactly one applies:
/// all content, a genre filter, an explicit whitelist, or an explicit blacklist of shows and movies.
/// </summary>
public enum SelectionMode
{
    /// <summary>All content in the library.</summary>
    AllContent = 0,

    /// <summary>Only items matching the configured genres.</summary>
    Genre = 1,

    /// <summary>Only the explicitly chosen shows and movies.</summary>
    Whitelist = 2,

    /// <summary>Everything in the library except the explicitly chosen shows and movies.</summary>
    Blacklist = 3
}
