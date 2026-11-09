namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The look of a placeholder block's card: a clip that loops seamlessly for as long as the block airs. Listed in the
/// order the settings page offers them: the default first and the custom file last.
/// </summary>
public enum PlaceholderStyle
{
    /// <summary>The tune-in card's flowing lines over its dark gradient.</summary>
    Default = 0,

    /// <summary>Analog television static with a rolling bar and scanlines.</summary>
    Static = 1,

    /// <summary>Broadcast colour bars with an occasional signal glitch.</summary>
    Error = 2,

    /// <summary>An underwater scene with rising bubbles and fish swimming across.</summary>
    Ocean = 3,

    /// <summary>Twinkling stars over hills, with a moon and a shooting star.</summary>
    NightSky = 4,

    /// <summary>Soft blobs drifting up and down, merging and parting.</summary>
    LavaLamp = 5,

    /// <summary>Rain falling in front of a blurred city at night.</summary>
    Rain = 6,

    /// <summary>Glowing embers rising through the dark from a fire below.</summary>
    Embers = 7,

    /// <summary>Northern lights rippling over hills under the stars.</summary>
    Aurora = 8,

    /// <summary>An image, GIF or video from a path on the server.</summary>
    Custom = 9
}
