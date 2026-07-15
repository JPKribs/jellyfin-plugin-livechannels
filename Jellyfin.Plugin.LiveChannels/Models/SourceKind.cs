namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// What a <see cref="LibrarySource"/> pulls from: a whole library (optionally narrowed) or a single collection.
/// </summary>
public enum SourceKind
{
    /// <summary>A library, narrowed by the source's selection, genres, and item lists.</summary>
    Library = 0,

    /// <summary>A collection (box set); its members are used, expanding series to their episodes.</summary>
    Collection = 1
}
