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

    /// <summary>Gets or sets the minimum official/parental rating allowed, by name (e.g. <c>TV-MA</c>). Empty means no floor.</summary>
    public string MinOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum official/parental rating allowed, by name (e.g. <c>TV-14</c>). Empty means no cap.</summary>
    public string MaxOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether items with no rating are included. When false, unrated content is dropped; when true (default) unrated content is always allowed regardless of the rating bounds.</summary>
    public bool IncludeUnrated { get; set; } = true;

    /// <summary>Gets or sets the rating at or below which a program is flagged as Kids in the guide (e.g. <c>G</c>). The program must still carry a rating to be flagged.</summary>
    public string KidsRatingThreshold { get; set; } = "G";

    /// <summary>Gets or sets how many consecutive episodes of a series to play as a block before moving on. 1 disables grouping.</summary>
    public int EpisodesPerBlock { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether multi-part episodes (e.g. "… (1)" / "… (2)") are kept adjacent and never split across a block boundary.</summary>
    public bool KeepMultiPartTogether { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether specials (season 0) are included.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>Gets or sets a value indicating whether home videos (loose <c>Video</c> items, as found in a Home Videos library) are included. Off by default so existing channels are unchanged; a Movies or Shows library has no such items.</summary>
    public bool IncludeHomeVideos { get; set; }

    /// <summary>Gets or sets a value indicating whether the channel's blocks are shuffled (deterministically) rather than ordered by name.</summary>
    public bool Shuffle { get; set; } = true;

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
}
