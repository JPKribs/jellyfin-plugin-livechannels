using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A single virtual channel. Its resolved items are looped on a deterministic wall-clock schedule and
/// presented in-process through Jellyfin's Live TV as a linear channel.
/// </summary>
public class Channel
{
    /// <summary>Gets or sets the stable identifier, used as the Live TV channel id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel number shown in the guide.</summary>
    public int Number { get; set; }

    /// <summary>Gets or sets an uploaded logo image as Base64 (no data URI prefix), set as the channel's image in Live TV. When empty, a generated square is used.</summary>
    public string LogoData { get; set; } = string.Empty;

    /// <summary>Gets or sets the content type of <see cref="LogoData"/>, e.g. <c>image/png</c>.</summary>
    public string LogoContentType { get; set; } = string.Empty;

    /// <summary>Gets or sets what the generated fallback logo draws in the centre when no image is uploaded: the channel number or a Material Icons symbol.</summary>
    public LogoStyle LogoStyle { get; set; } = LogoStyle.Number;

    /// <summary>Gets or sets the Material Icons name drawn in the centre when <see cref="LogoStyle"/> is <see cref="LogoStyle.Symbol"/> (e.g. <c>arrow_back</c>).</summary>
    public string LogoSymbol { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the generated logo shows the channel name along the bottom.</summary>
    public bool LogoShowName { get; set; } = true;

    /// <summary>Gets or sets the libraries (with optional genre or item filters) the channel pulls from. Content is the union of all sources.</summary>
    public List<LibrarySource> Sources { get; set; } = new();

    /// <summary>Gets or sets the required default-audio-track language, as a three-letter ISO code (e.g. <c>eng</c>, <c>jpn</c>). Empty means all languages are allowed; otherwise only content whose default audio track is this language is included.</summary>
    public string AudioLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum official/parental rating allowed, by name (e.g. <c>TV-MA</c>). Empty means no floor. Legacy single-band field; superseded by <see cref="RatingBlocks"/> and migrated into an all-day block when no blocks are set.</summary>
    public string MinOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum official/parental rating allowed, by name (e.g. <c>TV-14</c>). Empty means no cap. Legacy single-band field; superseded by <see cref="RatingBlocks"/> and migrated into an all-day block when no blocks are set.</summary>
    public string MaxOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether items with no rating are included. When false, unrated content is dropped; when true (default) unrated content is always allowed regardless of the rating bounds. Legacy single-band field; superseded by <see cref="RatingBlock.IncludeUnrated"/>.</summary>
    public bool IncludeUnrated { get; set; } = true;

    /// <summary>Gets or sets the time-of-day rating limits. Empty means every rating is allowed at every time. Each block sets a min/max rating and unrated rule for all day or a custom window; where blocks overlap the lowest min and lowest max win. When empty, the legacy <see cref="MinOfficialRating"/>/<see cref="MaxOfficialRating"/>/<see cref="IncludeUnrated"/> fields are migrated into a single all-day block (see <see cref="EffectiveRatingBlocks"/>).</summary>
    public List<RatingBlock> RatingBlocks { get; set; } = new();

    /// <summary>Gets or sets the transition window in minutes applied before every daypart boundary. An item starting within this many minutes of a boundary must satisfy the combined (lowest min, lowest max) constraint of the current and upcoming windows, so it stays compliant as it bleeds across. 0 disables the buffer. Set it at least as long as the channel's longest content.</summary>
    public int TransitionWindowMinutes { get; set; }

    /// <summary>Gets or sets the rating at or below which a program is flagged as Kids in the guide (e.g. <c>G</c>). The program must still carry a rating to be flagged.</summary>
    public string KidsRatingThreshold { get; set; } = "G";

    /// <summary>Gets or sets the production years a channel is limited to (e.g. <c>1990</c>…<c>1999</c> for a 90s channel). Empty means every year is allowed; otherwise only items whose production year is in this set are included, and items with no production year are dropped. For episodes this is the episode's own year, so a long-running series contributes only the episodes from the chosen years.</summary>
    public List<int> Years { get; set; } = new();

    /// <summary>Gets or sets the minimum community (audience) rating, on a 0–10 scale. 0 means no floor; otherwise items rated below this, and items with no community rating, are dropped.</summary>
    public double MinCommunityRating { get; set; }

    /// <summary>Gets or sets the minimum critic rating, on a 0–100 scale. 0 means no floor; otherwise items rated below this, and items with no critic rating, are dropped.</summary>
    public double MinCriticRating { get; set; }

    /// <summary>Gets or sets the studios/networks a channel is limited to (e.g. <c>HBO</c>). Empty means all studios; otherwise an item is included when it, or its series, carries any of these studios. Matched by name, case-insensitively.</summary>
    public List<string> Studios { get; set; } = new();

    /// <summary>Gets or sets the people (actors, directors, …) a channel is limited to. Empty means everyone; otherwise only items one of these people appears in are included. Matched by Jellyfin person id.</summary>
    public List<PersonRef> People { get; set; } = new();

    /// <summary>Gets or sets how many consecutive episodes of a series to play as a block before moving on. 1 disables grouping.</summary>
    public int EpisodesPerBlock { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether multi-part episodes (e.g. "… (1)" / "… (2)") are kept adjacent and never split across a block boundary.</summary>
    public bool KeepMultiPartTogether { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether specials (season 0) are included.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>Gets or sets a value indicating whether home videos (loose <c>Video</c> items, as found in a Home Videos library) are included. Off by default so existing channels are unchanged; a Movies or Shows library has no such items.</summary>
    public bool IncludeHomeVideos { get; set; }

    /// <summary>Gets or sets a value indicating whether the channel's blocks are shuffled (deterministically) rather than ordered by name. Legacy flag superseded by <see cref="LoopMode"/>; see <see cref="EffectiveLoopMode"/>.</summary>
    public bool Shuffle { get; set; } = true;

    /// <summary>Gets or sets how the channel arranges its loop: shuffle, alphabetical, or chronological. Supersedes <see cref="Shuffle"/>.</summary>
    public LoopMode LoopMode { get; set; } = LoopMode.Shuffle;

    /// <summary>Gets or sets a value indicating whether episodes within a series are shuffled rather than played in air order.</summary>
    public bool ShuffleEpisodes { get; set; }

    /// <summary>Gets or sets the content type the channel weights more heavily in its (shuffled) loop, or <see cref="Models.FavorKind.None"/>.</summary>
    public FavorKind FavorKind { get; set; } = FavorKind.None;

    /// <summary>Gets or sets how strongly <see cref="FavorKind"/> is favoured.</summary>
    public FavorStrength FavorStrength { get; set; } = FavorStrength.Moderate;

    /// <summary>
    /// Gets or sets which subtitle track is burned into the video. Burn-in is baked in for every viewer
    /// (a linear stream cannot carry selectable tracks).
    /// </summary>
    public SubtitleBurnInMode SubtitleBurnIn { get; set; } = SubtitleBurnInMode.Never;

    /// <summary>Gets or sets a value indicating whether the channel is served. Disabled channels are absent from Live TV and the guide.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The channel's effective loop order, migrating the legacy <see cref="Shuffle"/> flag: a channel saved before
    /// loop modes existed has <see cref="LoopMode"/> defaulting to <see cref="Models.LoopMode.Shuffle"/>, so when
    /// that default is in effect but Shuffle is off, the intent was alphabetical.
    /// </summary>
    /// <returns>The effective loop mode.</returns>
    public LoopMode EffectiveLoopMode()
        => LoopMode == LoopMode.Shuffle && !Shuffle ? LoopMode.Alphabetical : LoopMode;

    /// <summary>
    /// Returns the channel's rating blocks, migrating the legacy single-band fields into one all-day block when no
    /// blocks are configured. An empty result means every rating is allowed at every time.
    /// </summary>
    /// <returns>The effective rating blocks.</returns>
    public IReadOnlyList<RatingBlock> EffectiveRatingBlocks()
    {
        if (RatingBlocks.Count > 0)
        {
            return RatingBlocks;
        }

        if (!string.IsNullOrEmpty(MinOfficialRating) || !string.IsNullOrEmpty(MaxOfficialRating) || !IncludeUnrated)
        {
            return new[]
            {
                new RatingBlock
                {
                    MinOfficialRating = MinOfficialRating,
                    MaxOfficialRating = MaxOfficialRating,
                    IncludeUnrated = IncludeUnrated,
                    Period = RatingBlockPeriod.AllDay
                }
            };
        }

        return Array.Empty<RatingBlock>();
    }
}
