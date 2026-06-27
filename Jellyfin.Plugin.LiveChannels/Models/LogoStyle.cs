namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// What the generated fallback logo draws in the centre when a channel has no uploaded image.
/// </summary>
public enum LogoStyle
{
    /// <summary>The channel number.</summary>
    Number = 0,

    /// <summary>A Material Icons symbol named by <see cref="Channel.LogoSymbol"/>.</summary>
    Symbol = 1
}
