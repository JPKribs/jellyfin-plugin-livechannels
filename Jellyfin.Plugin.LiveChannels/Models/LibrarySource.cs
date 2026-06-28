using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// One library a channel pulls from, with an optional genre filter and an item whitelist or blacklist.
/// A channel's content is the union of all its sources.
/// </summary>
public class LibrarySource
{
    /// <summary>Gets or sets the library (collection folder) id content is scoped to.</summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library display name, kept for the admin UI.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the genres the library is filtered by. Empty means no genre filter.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether every genre must match (AND) rather than any (OR).</summary>
    public bool MatchAllGenres { get; set; }

    /// <summary>Gets or sets genres to exclude. Any item carrying one of these (on itself or, for an episode, on
    /// its series) is dropped even if it matched the included genres. Empty means no exclusions.</summary>
    public List<string> ExcludeGenres { get; set; } = new();

    /// <summary>Gets or sets how the library is narrowed: all content, a genre filter, a whitelist, or a blacklist. Exactly one applies.</summary>
    public SelectionMode Selection { get; set; } = SelectionMode.AllContent;

    /// <summary>Gets or sets the explicitly chosen show and movie ids used when <see cref="Selection"/> is a whitelist or blacklist. Series expand to their episodes.</summary>
    public List<Guid> ItemIds { get; set; } = new();
}
