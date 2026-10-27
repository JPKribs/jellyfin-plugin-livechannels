using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// What an item's Up Next bumper says, decided from the item alone: the title, the tag before the second line (an
/// episode's season and episode), the second line (an episode's name or a movie's tagline), the details line, the
/// review score, and the parental rating for the corner badge. Any of them but the title may be missing, and a
/// missing one is simply left out of the card.
/// </summary>
/// <param name="Title">The big title: the series for an episode, otherwise the item's own name.</param>
/// <param name="Tag">The season and episode tag (<c>S2 · E5</c>, <c>E5</c>, or <c>Special</c>), or <c>null</c>.</param>
/// <param name="Subtitle">The episode name, or a movie's tagline, or <c>null</c>.</param>
/// <param name="SubtitleIsTagline">Whether the subtitle is a tagline (set in italics) rather than an episode name.</param>
/// <param name="Details">Year, genres and runtime (a video's date and runtime), or <c>null</c>.</param>
/// <param name="Score">The community rating out of ten (<c>8.4</c>), or <c>null</c>.</param>
/// <param name="Rating">The parental rating for the corner badge, or <c>null</c> for no badge.</param>
internal sealed record BumperContent(string Title, string? Tag, string? Subtitle, bool SubtitleIsTagline, string? Details, string? Score, string? Rating)
{
    /// <summary>
    /// The card for an item. An episode shows its series as the title, its season and episode as the tag (season
    /// 0 is "Special"; of a range only the first episode number is kept, which is the one Jellyfin records), its
    /// own name as the second line, and its series' parental rating (its own only when the series has none). A
    /// movie shows its tagline as the second line. Anything else (a home video, a music video) shows its title and
    /// details only.
    /// </summary>
    /// <param name="program">The item.</param>
    /// <returns>Its card.</returns>
    public static BumperContent For(ProgramEntry program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var episode = IsEpisode(program);
        var movie = program.IsMovie || string.Equals(program.ItemKind, "Movie", StringComparison.Ordinal);
        var name = Clean(program.RawName) ?? Clean(program.Title) ?? "Untitled";

        var title = episode ? Clean(program.SeriesName) ?? name : name;
        var tag = episode ? EpisodeTag(program.SeasonNumber, program.EpisodeNumber) : null;
        var subtitle = episode ? (string.Equals(name, title, StringComparison.Ordinal) ? null : name) : movie ? Clean(program.Tagline) : null;
        var rating = episode ? Clean(program.SeriesOfficialRating) ?? Clean(program.OfficialRating) : Clean(program.OfficialRating);
        var score = program.CommunityRating is > 0 and <= 10 and var value
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : null;

        return new BumperContent(title, tag, subtitle, !episode && subtitle is not null, DetailsLine(program, episode || movie), score, rating);
    }

    /// <summary>The season and episode tag: <c>Special</c> for season 0, <c>S2 · E5</c>, <c>E5</c> with no season, or <c>null</c> with no episode number.</summary>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The (first) episode number.</param>
    /// <returns>The tag, or <c>null</c>.</returns>
    internal static string? EpisodeTag(int? season, int? episode)
    {
        if (season == 0)
        {
            return "Special";
        }

        if (episode is not { } e || e < 0)
        {
            return null;
        }

        return season is { } s and > 0
            ? "S" + s.ToString(CultureInfo.InvariantCulture) + " · E" + e.ToString(CultureInfo.InvariantCulture)
            : "E" + e.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A runtime as the card prints it: <c>44 min</c>, <c>2 h</c>, or <c>1 h 52 min</c>.</summary>
    /// <param name="ticks">The runtime in ticks.</param>
    /// <returns>The runtime, or <c>null</c> when it is under a minute.</returns>
    internal static string? Runtime(long ticks)
    {
        var minutes = (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes);
        if (minutes < 1)
        {
            return null;
        }

        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours.ToString(CultureInfo.InvariantCulture) + " h" + (rest == 0 ? string.Empty : " " + rest.ToString(CultureInfo.InvariantCulture) + " min");
    }

    private static bool IsEpisode(ProgramEntry program)
        => program.ItemKind is { } kind
            ? string.Equals(kind, "Episode", StringComparison.Ordinal)
            : program.SeriesId is not null;

    // Year, genres and runtime for an episode or movie; the date and runtime for anything else (a home video's
    // year alone says little, its date says which trip it was).
    private static string? DetailsLine(ProgramEntry program, bool titled)
    {
        var parts = new List<string>();
        if (titled)
        {
            if (program.Year is { } year and > 0)
            {
                parts.Add(year.ToString(CultureInfo.InvariantCulture));
            }

            var genres = program.Genres.Select(Clean).Where(g => g is not null).ToList();
            if (genres.Count > 0)
            {
                parts.Add(string.Join(", ", genres));
            }
        }
        else if ((program.PremiereDate ?? (program.DateAdded == default ? null : program.DateAdded)) is { } date)
        {
            parts.Add(date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture));
        }
        else if (program.Year is { } year and > 0)
        {
            parts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        if (Runtime(program.DurationTicks) is { } runtime)
        {
            parts.Add(runtime);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // Trims a piece of metadata, collapsing line breaks and runs of spaces (a drawn line has no room for either),
    // and treats blank as missing.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
