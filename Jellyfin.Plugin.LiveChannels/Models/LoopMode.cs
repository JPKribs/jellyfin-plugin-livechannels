namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// How a channel arranges its blocks (series and standalone items) into the looping schedule.
/// </summary>
public enum LoopMode
{
    /// <summary>Shuffle the blocks deterministically so no single series dominates.</summary>
    Shuffle = 0,

    /// <summary>Order the blocks alphabetically by title.</summary>
    Alphabetical = 1,

    /// <summary>Order the blocks by release or air date, oldest first.</summary>
    Chronological = 2
}
