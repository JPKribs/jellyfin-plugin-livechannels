using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.LiveChannels.Models;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Proves <see cref="ProgramEntry"/> round-trips through System.Text.Json with the exact options the on-disk
/// schedule cache uses. If this breaks, the cache silently fails to read back and every tune-in rebuilds.
/// </summary>
public class ScheduleCacheSerializationTests
{
    // Mirrors ChannelService.ScheduleCacheJson.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ProgramEntry_RoundTrips_AllFields()
    {
        var original = new ProgramEntry(Guid.NewGuid(), "American Dad! - Pilot", "An alien lives in the attic.", 13_200_000_000L, "/media/ad/s01e01.mkv")
        {
            Year = 2005,
            OfficialRating = "TV-14",
            Genres = new[] { "Animation", "Comedy" },
            SeasonNumber = 1,
            EpisodeNumber = 1,
            IsMovie = false,
            IsKids = false,
            SeriesId = Guid.NewGuid(),
            SeriesName = "American Dad!",
            RawName = "Pilot",
            GuideImagePath = "/cache/img/ad.jpg",
            SourceHeight = 1080,
            DateAdded = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CommunityRating = 7.3f,
            PremiereDate = new DateTime(2005, 2, 6, 0, 0, 0, DateTimeKind.Utc),
            IsHdr = true,
            IsInterlaced = true,
            IsTenBit = true,
            DefaultAudioOrdinal = 2,
            DefaultAudioLanguage = "jpn",
            Subtitles = new[]
            {
                new SubtitleStreamInfo { RelativeIndex = 0, AbsoluteIndex = 3, IsForced = true, IsDefault = false, IsText = true },
                new SubtitleStreamInfo { RelativeIndex = 1, AbsoluteIndex = 4, IsForced = false, IsDefault = true, IsText = false }
            }
        };

        var json = JsonSerializer.Serialize(new List<ProgramEntry> { original }, Options);
        var restored = JsonSerializer.Deserialize<List<ProgramEntry>>(json, Options);

        Assert.NotNull(restored);
        var entry = Assert.Single(restored!);
        Assert.Equal(original.ItemId, entry.ItemId);
        Assert.Equal(original.Title, entry.Title);
        Assert.Equal(original.Overview, entry.Overview);
        Assert.Equal(original.DurationTicks, entry.DurationTicks);
        Assert.Equal(original.Path, entry.Path);
        Assert.Equal(original.Year, entry.Year);
        Assert.Equal(original.OfficialRating, entry.OfficialRating);
        Assert.Equal(original.Genres, entry.Genres);
        Assert.Equal(original.SeasonNumber, entry.SeasonNumber);
        Assert.Equal(original.EpisodeNumber, entry.EpisodeNumber);
        Assert.Equal(original.IsMovie, entry.IsMovie);
        Assert.Equal(original.IsKids, entry.IsKids);
        Assert.Equal(original.SeriesId, entry.SeriesId);
        Assert.Equal(original.SeriesName, entry.SeriesName);
        Assert.Equal(original.RawName, entry.RawName);
        Assert.Equal(original.GuideImagePath, entry.GuideImagePath);
        Assert.Equal(original.SourceHeight, entry.SourceHeight);
        Assert.Equal(original.DateAdded, entry.DateAdded);
        Assert.Equal(original.CommunityRating, entry.CommunityRating);
        Assert.Equal(original.PremiereDate, entry.PremiereDate);
        Assert.True(entry.IsHdr);
        Assert.True(entry.IsInterlaced);
        Assert.True(entry.IsTenBit);
        Assert.Equal(2, entry.DefaultAudioOrdinal);
        Assert.Equal("jpn", entry.DefaultAudioLanguage);
        Assert.Equal(2, entry.Subtitles.Count);
        Assert.True(entry.Subtitles[0].IsForced);
        Assert.True(entry.Subtitles[0].IsText);
        Assert.Equal(3, entry.Subtitles[0].AbsoluteIndex);
        Assert.True(entry.Subtitles[1].IsDefault);
        Assert.False(entry.Subtitles[1].IsText);
        Assert.Equal(1, entry.Subtitles[1].RelativeIndex);
    }

    [Fact]
    public void ChannelLoop_RoundTrips_AsList()
    {
        // Each channel's schedule file holds just that channel's program loop as a List<ProgramEntry> (named by
        // channel number, e.g. schedule/0.json); prove the ordered loop round-trips so a tune-in reads it back.
        var loop = new List<ProgramEntry>
        {
            new(Guid.NewGuid(), "B", null, 2000, "/b.mkv"),
            new(Guid.NewGuid(), "C", null, 3000, "/c.mkv")
        };

        var json = JsonSerializer.Serialize(loop, Options);
        var restored = JsonSerializer.Deserialize<List<ProgramEntry>>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("B", restored[0].Title);
        Assert.Equal("C", restored[1].Title);
    }

    [Fact]
    public void ProgramEntry_RoundTrips_NullablesUnset()
    {
        var original = new ProgramEntry(Guid.NewGuid(), "Some Movie", null, 72_000_000_000L, "/media/movie.mkv");

        var json = JsonSerializer.Serialize(new List<ProgramEntry> { original }, Options);
        var entry = Assert.Single(JsonSerializer.Deserialize<List<ProgramEntry>>(json, Options)!);

        Assert.Equal(original.ItemId, entry.ItemId);
        Assert.Null(entry.Overview);
        Assert.Null(entry.Year);
        Assert.Null(entry.SeriesId);
        Assert.Null(entry.CommunityRating);
        Assert.Null(entry.PremiereDate);
        Assert.Empty(entry.Genres);
        Assert.False(entry.IsHdr);
        Assert.False(entry.IsInterlaced);
        Assert.False(entry.IsTenBit);
        Assert.Null(entry.DefaultAudioOrdinal);
        Assert.Null(entry.DefaultAudioLanguage);
        Assert.Empty(entry.Subtitles);
    }
}
