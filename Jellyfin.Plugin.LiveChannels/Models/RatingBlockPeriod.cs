namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// Whether a <see cref="RatingBlock"/> applies all day or only during a custom time-of-day window.
/// </summary>
public enum RatingBlockPeriod
{
    /// <summary>The block applies for the whole day.</summary>
    AllDay = 0,

    /// <summary>The block applies only during its start-to-end time-of-day window, which may wrap past midnight.</summary>
    Custom = 1
}
