namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// One rating limit that applies all day or during a time-of-day window. A channel may carry several. Where
/// two are active at once, the effective limit is the lowest minimum and lowest maximum across them, and
/// unrated content is allowed only when every active block allows it. With no blocks at all, and during any
/// time no block covers, every rating is allowed.
/// </summary>
public class RatingBlock
{
    /// <summary>Gets or sets the minimum official/parental rating allowed by this block, by name (e.g. <c>PG</c>). Empty means no floor.</summary>
    public string MinOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum official/parental rating allowed by this block, by name (e.g. <c>TV-14</c>). Empty means no cap.</summary>
    public string MaxOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether items with no rating are allowed while this block is active. True by default.</summary>
    public bool IncludeUnrated { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the guide tags the channel as kids content while this block is active.</summary>
    public bool IsKids { get; set; }

    /// <summary>Gets or sets whether the block applies all day or only during its custom window.</summary>
    public RatingBlockPeriod Period { get; set; } = RatingBlockPeriod.AllDay;

    /// <summary>Gets or sets the window start as minutes since local midnight (0-1439). Used only when <see cref="Period"/> is <see cref="RatingBlockPeriod.Custom"/>.</summary>
    public int StartMinutes { get; set; }

    /// <summary>Gets or sets the window end as minutes since local midnight (0-1439), exclusive. When the end is at or before the start, the window wraps past midnight (e.g. 22:00-04:00). Used only when <see cref="Period"/> is <see cref="RatingBlockPeriod.Custom"/>.</summary>
    public int EndMinutes { get; set; }

    /// <summary>
    /// Whether the block is active at the given minute of the local day (0-1439). A custom window is half-open
    /// <c>[start, end)</c> and wraps past midnight when the end is at or before the start.
    /// </summary>
    /// <param name="minuteOfDay">The minute of the day, 0-1439.</param>
    /// <returns><c>true</c> when the block applies at that minute.</returns>
    public bool ActiveAt(int minuteOfDay)
    {
        if (Period == RatingBlockPeriod.AllDay)
        {
            return true;
        }

        if (StartMinutes == EndMinutes)
        {
            return false;
        }

        return StartMinutes < EndMinutes
            ? minuteOfDay >= StartMinutes && minuteOfDay < EndMinutes
            : minuteOfDay >= StartMinutes || minuteOfDay < EndMinutes;
    }
}
