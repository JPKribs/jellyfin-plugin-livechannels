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

    /// <summary>Gets the parent series' official/parental rating for an episode, or <c>null</c>. The Up Next bumper shows an episode's series rating (an episode's own is often missing).</summary>
    public string? SeriesOfficialRating { get; init; }

    /// <summary>Gets the item's tagline (a movie's one-line pitch), or <c>null</c>, shown on its Up Next bumper.</summary>
    public string? Tagline { get; init; }

    /// <summary>Gets a music video's artists, shown under its title on its Up Next bumper; empty for anything else, and in schedules cached before they were recorded.</summary>
    public IReadOnlyList<string> Artists { get; init; } = Array.Empty<string>();

    /// <summary>Gets the library item kind (e.g. <c>Episode</c>, <c>Movie</c>, <c>MusicVideo</c>, <c>Video</c>), or <c>null</c> in schedules cached before it was recorded. Chooses the Up Next bumper's layout.</summary>
    public string? ItemKind { get; init; }

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

    /// <summary>Gets the file-system path to the program's portrait artwork (the poster), or <c>null</c>. For an episode this is the series poster, since an episode's own primary image is a landscape still.</summary>
    public string? PrimaryImagePath { get; init; }

    /// <summary>Gets the file-system path to the program's landscape artwork, or <c>null</c>: the item's thumb, its series' thumb, or an episode's own primary still.</summary>
    public string? ThumbImagePath { get; init; }

    /// <summary>Gets the file-system path to the program's backdrop, or <c>null</c>, falling back to the series' backdrop for an episode.</summary>
    public string? BackdropImagePath { get; init; }

    /// <summary>Gets the file-system path to the program's clear logo, or <c>null</c>, falling back to the series' logo for an episode.</summary>
    public string? LogoImagePath { get; init; }

    /// <summary>Gets the source video height in pixels (0 when unknown), used to choose the decode pipeline (software concat vs per-item hardware decode for high-resolution sources).</summary>
    public int SourceHeight { get; init; }

    /// <summary>Gets the date the item was added to the library, used to flag recently-added content as new in the guide.</summary>
    public DateTime DateAdded { get; init; }

    /// <summary>
    /// How recently an item must have been added to the library for the guide to call it new (a premiere rather
    /// than a repeat), and for a "new episodes only" series recording to take it.
    /// </summary>
    public static readonly TimeSpan NewWindow = TimeSpan.FromDays(14);

    /// <summary>Whether the item counts as new at the given time (see <see cref="NewWindow"/>).</summary>
    /// <param name="nowUtc">The time.</param>
    /// <returns>Whether it is new.</returns>
    public bool IsNewAt(DateTime nowUtc) => DateAdded >= nowUtc - NewWindow;

    /// <summary>Gets the community/critic rating (0-10), surfaced as the guide's star rating. <c>null</c> when unrated.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the original release/air date, surfaced as the guide's original air date. <c>null</c> when unknown.</summary>
    public DateTime? PremiereDate { get; init; }

    /// <summary>Gets a value indicating whether the source video carries an HDR (PQ/HLG) transfer, so the stream pipeline tone-maps it to SDR. Probed once at guide refresh and cached here, so the live stream never re-queries the media streams to decide its decode pipeline.</summary>
    public bool IsHdr { get; init; }

    /// <summary>Gets the position, among the item's audio streams ordered by index, of the track Jellyfin marks as default (0 when none, <c>null</c> when unknown), so the stream pipeline maps the same audio Jellyfin would play. Probed once at guide refresh and cached here.</summary>
    public int? DefaultAudioOrdinal { get; init; }

    /// <summary>Gets the source video codec (e.g. <c>h264</c>, <c>hevc</c>), or <c>null</c> when unknown (an unprobed item, or a schedule cached before this was recorded). Probed once at guide refresh and cached here.</summary>
    public string? SourceVideoCodec { get; init; }

    /// <summary>Gets the source video's bit depth (8, 10, 12), or <c>null</c> when unknown (an unprobed item, or a schedule cached before this was recorded). With <see cref="SourcePixelFormat"/> it decides whether Jellyfin's hardware decoding settings allow decoding the item on the GPU. Probed once at guide refresh and cached here.</summary>
    public int? SourceBitDepth { get; init; }

    /// <summary>Gets the source video's pixel format (e.g. <c>yuv420p10le</c>, <c>yuv422p10le</c>), or <c>null</c> when unknown. A 4:2:2 or 4:4:4 HEVC source is the range extension profile, which Jellyfin's settings allow on the GPU separately. Probed once at guide refresh and cached here.</summary>
    public string? SourcePixelFormat { get; init; }

    /// <summary>Gets the source video's tagged colour space (e.g. <c>bt709</c>, <c>smpte170m</c>), or <c>null</c> when untagged or unknown, so the stream pipeline converts the item from the right matrix into the channel's bt709. Probed once at guide refresh and cached here.</summary>
    public string? SourceColorSpace { get; init; }

    /// <summary>Gets how far the source video is rotated for display, in degrees (0, 90, 180 or 270; a phone held upright records 90). ffmpeg turns such a picture upright only when it decodes in software, so these items never decode on a GPU that keeps frames in VRAM. Probed once at guide refresh and cached here; zero in schedules cached before this was recorded.</summary>
    public int SourceRotation { get; init; }

    /// <summary>Gets the codec of the default audio track (e.g. <c>aac</c>, <c>ac3</c>), or <c>null</c> when unknown or the item has no audio. Probed once at guide refresh and cached here.</summary>
    public string? SourceAudioCodec { get; init; }

    /// <summary>Gets the channel count of the default audio track, or <c>null</c> when unknown or the item has no audio. Probed once at guide refresh and cached here.</summary>
    public int? SourceAudioChannels { get; init; }

    /// <summary>Gets a value indicating whether the item has a video stream but no audio stream at all, so the stream pipeline gives it a silent track. <c>false</c> when unknown (an unprobed item, or a schedule cached before this was recorded), which keeps mapping the item's own audio. Probed once at guide refresh and cached here.</summary>
    public bool IsVideoOnly { get; init; }

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

    /// <summary>Gets a value indicating whether the subtitle is a separate file beside the media rather than a track inside it. An external subtitle has no stream index within the media file, so it can only be burned from the file Jellyfin resolves for it.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Gets the subtitle's language tag (e.g. <c>eng</c>), or <c>null</c> when untagged (or cached before languages were recorded).</summary>
    public string? Language { get; init; }
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

    /// <summary>Gets the Up Next bumper that opens the slot (zero when there is none). The item itself starts this long after <see cref="Start"/>.</summary>
    public TimeSpan Lead { get; init; }

    /// <summary>Gets the UTC time the item itself starts: the slot start plus the bumper lead.</summary>
    public DateTime ContentStart => Start + Lead;
}
