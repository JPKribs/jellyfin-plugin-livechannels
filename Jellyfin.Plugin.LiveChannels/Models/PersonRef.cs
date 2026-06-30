using System;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A person (actor, director, …) a channel is filtered by, stored as the Jellyfin person id with the name kept
/// for the admin UI. Matching is by id, so a rename in the library never breaks the filter.
/// </summary>
public class PersonRef
{
    /// <summary>Gets or sets the Jellyfin person id, used to match items the person appears in.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the person's display name, kept for the admin UI.</summary>
    public string Name { get; set; } = string.Empty;
}
