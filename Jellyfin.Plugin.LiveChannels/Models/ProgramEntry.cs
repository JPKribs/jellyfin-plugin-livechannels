using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A single resolved item in a channel's loop: enough metadata to schedule it, stream it, and describe it
/// in the guide. Decoupled from Jellyfin's <c>BaseItem</c> so the scheduling logic can be unit tested.
/// </summary>
public sealed class ProgramEntry
{
    /// <summary>Initializes a new instance of the <see cref="ProgramEntry"/> class.</summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="title">The program title shown in the guide.</param>
    /// <param name="overview">The program description, or <c>null</c>.</param>
    /// <param name="durationTicks">The runtime in ticks. Must be greater than zero to be schedulable.</param>
    /// <param name="path">The media file path ffmpeg reads, or <c>null</c> when unavailable.</param>
    public ProgramEntry(Guid itemId, string title, string? overview, long durationTicks, string? path)
    {
        ItemId = itemId;
        Title = title;
        Overview = overview;
        DurationTicks = durationTicks;
        Path = path;
    }

    /// <summary>Gets the Jellyfin item id.</summary>
    public Guid ItemId { get; }

    /// <summary>Gets the program title.</summary>
    public string Title { get; }

    /// <summary>Gets the program description, or <c>null</c>.</summary>
    public string? Overview { get; }

    /// <summary>Gets the runtime in ticks.</summary>
    public long DurationTicks { get; }

    /// <summary>Gets the media file path, or <c>null</c>.</summary>
    public string? Path { get; }

    /// <summary>Gets the production year, used for the guide's <c>date</c>.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the official/parental rating, used for the guide's <c>rating</c>.</summary>
    public string? OfficialRating { get; init; }

    /// <summary>Gets the genres, used for the guide's <c>category</c> entries.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Gets the season number for episodes, used for the guide's <c>episode-num</c>.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number for episodes, used for the guide's <c>episode-num</c>.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets a value indicating whether the item is a movie, used to flag the program as a movie in the guide.</summary>
    public bool IsMovie { get; init; }

    /// <summary>Gets a value indicating whether the item rates at or below the channel's kids threshold, used to flag the program as kids content in the guide.</summary>
    public bool IsKids { get; init; }

    /// <summary>Gets the parent series id for episodes, used to group a series into blocks. <c>null</c> for standalone items.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name for episodes, used to order and label blocks.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the item's own name (the episode title, without the series prefix), used to detect multi-part siblings.</summary>
    public string? RawName { get; init; }

    /// <summary>Gets a value indicating whether the item has a primary image, so the guide only links artwork that exists.</summary>
    public bool HasPrimaryImage { get; init; }

    /// <summary>Gets the file-system path to the item's primary image, or <c>null</c>, used to show episode artwork in the guide.</summary>
    public string? PrimaryImagePath { get; init; }

    /// <summary>Gets the source video height in pixels (0 when unknown), used to choose the decode pipeline (software concat vs per-item hardware decode for high-resolution sources).</summary>
    public int SourceHeight { get; init; }

    /// <summary>Gets the date the item was added to the library, used to flag recently-added content as new in the guide.</summary>
    public DateTime DateAdded { get; init; }
}

/// <summary>
/// A <see cref="ProgramEntry"/> placed on the timeline with concrete start and stop times.
/// </summary>
public sealed class ScheduledProgram
{
    /// <summary>Initializes a new instance of the <see cref="ScheduledProgram"/> class.</summary>
    /// <param name="program">The program being aired.</param>
    /// <param name="start">The UTC start time.</param>
    /// <param name="stop">The UTC stop time.</param>
    public ScheduledProgram(ProgramEntry program, DateTime start, DateTime stop)
    {
        Program = program;
        Start = start;
        Stop = stop;
    }

    /// <summary>Gets the program being aired.</summary>
    public ProgramEntry Program { get; }

    /// <summary>Gets the UTC start time.</summary>
    public DateTime Start { get; }

    /// <summary>Gets the UTC stop time.</summary>
    public DateTime Stop { get; }
}
