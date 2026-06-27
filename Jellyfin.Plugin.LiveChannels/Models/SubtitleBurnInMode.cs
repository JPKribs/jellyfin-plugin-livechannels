namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// Which subtitle track, if any, a channel burns into its video. Burn-in is hard-rendered into the picture
/// for every viewer, since a linear stream cannot carry selectable subtitle tracks.
/// </summary>
public enum SubtitleBurnInMode
{
    /// <summary>Never burn in subtitles.</summary>
    Never = 0,

    /// <summary>Burn in the item's forced subtitle track when it has one (e.g. for foreign-language scenes).</summary>
    Forced = 1,

    /// <summary>Always burn in a subtitle track: the forced one if present, otherwise the default or first available.</summary>
    Always = 2
}
