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

    /// <summary>Gets the item's numeric inherited parental score (<c>null</c> when unrated), cached at guide refresh so time-of-day rating blocks can be applied when the schedule is built without re-querying the library.</summary>
    public int? ParentalRatingValue { get; init; }

    /// <summary>Gets the genres, used for the guide's <c>category</c> entries.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Gets the season number for episodes, used for the guide's <c>episode-num</c>.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number for episodes, used for the guide's <c>episode-num</c>.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets a value indicating whether the item is a movie, used to flag the program as a movie in the guide.</summary>
    public bool IsMovie { get; init; }

    /// <summary>Gets the parent series id for episodes, used to group a series into blocks. <c>null</c> for standalone items.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name for episodes, used to order and label blocks.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the item's own name (the episode title, without the series prefix), used to detect multi-part siblings.</summary>
    public string? RawName { get; init; }

    /// <summary>Gets the file-system path to the item's landscape guide artwork, or <c>null</c>: a movie's backdrop, otherwise the primary image (episode and music-video primaries are already landscape thumbnails).</summary>
    public string? GuideImagePath { get; init; }

    /// <summary>Gets the source video height in pixels (0 when unknown), used to choose the decode pipeline (software concat vs per-item hardware decode for high-resolution sources).</summary>
    public int SourceHeight { get; init; }

    /// <summary>Gets the date the item was added to the library, used to flag recently-added content as new in the guide.</summary>
    public DateTime DateAdded { get; init; }

    /// <summary>Gets the community/critic rating (0-10), surfaced as the guide's star rating. <c>null</c> when unrated.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the original release/air date, surfaced as the guide's original air date. <c>null</c> when unknown.</summary>
    public DateTime? PremiereDate { get; init; }

    /// <summary>Gets a value indicating whether the source video carries an HDR (PQ/HLG) transfer, so the stream pipeline tone-maps it to SDR. Probed once at guide refresh and cached here, so the live stream never re-queries the media streams to decide its decode pipeline.</summary>
    public bool IsHdr { get; init; }

    /// <summary>Gets the position, among the item's audio streams ordered by index, of the track Jellyfin marks as default (0 when none, <c>null</c> when unknown), so the stream pipeline maps the same audio Jellyfin would play. Probed once at guide refresh and cached here.</summary>
    public int? DefaultAudioOrdinal { get; init; }

    /// <summary>Gets the three-letter language of the default audio track, or <c>null</c>, used by the Forced-only subtitle rule to burn in subtitles for foreign-language audio. Probed once at guide refresh and cached here.</summary>
    public string? DefaultAudioLanguage { get; init; }

    /// <summary>Gets the item's subtitle streams (in index order), enough to pick a burn-in track without re-reading the media streams at tune-in.</summary>
    public IReadOnlyList<SubtitleStreamInfo> Subtitles { get; init; } = Array.Empty<SubtitleStreamInfo>();
}

/// <summary>
/// The minimal description of one subtitle stream needed to choose a burn-in track, cached on the
/// <see cref="ProgramEntry"/> at guide refresh so the live stream never re-reads the media streams.
/// </summary>
public sealed class SubtitleStreamInfo
{
    /// <summary>Gets the stream's index among the item's subtitle streams (ordered by absolute index), which ffmpeg's <c>s:N</c> / <c>si=N</c> specifiers use.</summary>
    public int RelativeIndex { get; init; }

    /// <summary>Gets the stream's absolute index among all of the item's media streams, used when extracting the subtitle for a deep tune-in.</summary>
    public int AbsoluteIndex { get; init; }

    /// <summary>Gets a value indicating whether the stream is flagged forced.</summary>
    public bool IsForced { get; init; }

    /// <summary>Gets a value indicating whether the stream is flagged default.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets a value indicating whether the stream is text-based (libass) rather than bitmap (PGS/VOBSUB).</summary>
    public bool IsText { get; init; }
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
