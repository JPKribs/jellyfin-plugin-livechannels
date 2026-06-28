namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// How strongly a channel favours its chosen content type, expressed as the share of the loop it aims for.
/// </summary>
public enum FavorStrength
{
    /// <summary>A modest lean toward the favoured type.</summary>
    Slight = 0,

    /// <summary>The favoured type takes the larger share.</summary>
    Moderate = 1,

    /// <summary>The favoured type dominates the loop.</summary>
    Heavy = 2
}
