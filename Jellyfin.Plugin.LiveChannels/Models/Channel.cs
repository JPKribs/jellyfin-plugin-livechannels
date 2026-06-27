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

    /// <summary>Gets or sets an uploaded logo image as Base64 (no data URI prefix), set as the channel's image in Live TV. When empty, a generated square (channel number and title) is used.</summary>
    public string LogoData { get; set; } = string.Empty;

    /// <summary>Gets or sets the content type of <see cref="LogoData"/>, e.g. <c>image/png</c>.</summary>
    public string LogoContentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the libraries (with optional genre or item filters) the channel pulls from. Content is the union of all sources.</summary>
    public List<LibrarySource> Sources { get; set; } = new();

    /// <summary>Gets or sets the maximum official/parental rating allowed, by name (e.g. <c>TV-14</c>). Empty means no cap.</summary>
    public string MaxOfficialRating { get; set; } = string.Empty;

    /// <summary>Gets or sets how many consecutive episodes of a series to play as a block before moving on. 1 disables grouping.</summary>
    public int EpisodesPerBlock { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether multi-part episodes (e.g. "… (1)" / "… (2)") are kept adjacent and never split across a block boundary.</summary>
    public bool KeepMultiPartTogether { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether specials (season 0) are included.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>Gets or sets a value indicating whether the channel's blocks are shuffled (deterministically) rather than ordered by name.</summary>
    public bool Shuffle { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether episodes within a series are shuffled rather than played in air order.</summary>
    public bool ShuffleEpisodes { get; set; }

    /// <summary>
    /// Gets or sets which subtitle track is burned into the video. Burn-in is baked in for every viewer
    /// (a linear stream cannot carry selectable tracks).
    /// </summary>
    public SubtitleBurnInMode SubtitleBurnIn { get; set; } = SubtitleBurnInMode.Never;

    /// <summary>
    /// Gets or sets the Live TV guide categories this channel's programs are tagged with — any of
    /// <c>Movies</c>, <c>Sports</c>, <c>Kids</c>, <c>News</c>. Empty (the default) leaves programs uncategorised.
    /// These map to the matching <c>ProgramInfo</c> flags so the guide's category filters pick the channel up.
    /// </summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the channel is served. Disabled channels are absent from Live TV and the guide.</summary>
    public bool Enabled { get; set; } = true;
}
