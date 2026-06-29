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
            PremiereDate = new DateTime(2005, 2, 6, 0, 0, 0, DateTimeKind.Utc)
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
    }

    [Fact]
    public void ScheduleMap_RoundTrips_PerChannel()
    {
        // The on-disk schedule.json is a channel-id -> program-loop map; prove it round-trips so a tune-in reads
        // back the right channel's schedule.
        var map = new Dictionary<string, List<ProgramEntry>>
        {
            ["livechannels-popular"] = new() { new ProgramEntry(Guid.NewGuid(), "A", null, 1000, "/a.mkv") },
            ["3f2504e0-4f89-41d3-9a0c-0305e82c3301"] = new()
            {
                new ProgramEntry(Guid.NewGuid(), "B", null, 2000, "/b.mkv"),
                new ProgramEntry(Guid.NewGuid(), "C", null, 3000, "/c.mkv")
            }
        };

        var json = JsonSerializer.Serialize(map, Options);
        var restored = JsonSerializer.Deserialize<Dictionary<string, List<ProgramEntry>>>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Single(restored["livechannels-popular"]);
        Assert.Equal(2, restored["3f2504e0-4f89-41d3-9a0c-0305e82c3301"].Count);
        Assert.Equal("C", restored["3f2504e0-4f89-41d3-9a0c-0305e82c3301"][1].Title);
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
    }
}
