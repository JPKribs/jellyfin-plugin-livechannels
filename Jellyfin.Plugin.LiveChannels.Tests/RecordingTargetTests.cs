using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the recording materializer's pure pieces: program id parsing, DVR-style naming, and destination
/// building that mirrors the built-in DVR's folder scheme (so recordings land in the same libraries).
/// </summary>
public class RecordingTargetTests
{
    private const string Root = "/data/livetv/recordings";

    private static readonly IFileSystem FileSystem = CreateFileSystem();

    private static IFileSystem CreateFileSystem()
    {
        var fs = Substitute.For<IFileSystem>();
        fs.GetValidFilename(Arg.Any<string>()).Returns(call => call.Arg<string>().Replace(':', ' ').Replace('/', ' '));
        fs.AreEqual(Arg.Any<string>(), Arg.Any<string>()).Returns(call => string.Equals(call.ArgAt<string>(0), call.ArgAt<string>(1), StringComparison.OrdinalIgnoreCase));
        return fs;
    }

    private static ProgramEntry Episode() => new(Guid.NewGuid(), "Andor - Reckoning", "overview", 1_000_000, "/media/andor/s01e03.mkv")
    {
        SeriesId = Guid.NewGuid(),
        SeriesName = "Andor",
        RawName = "Reckoning",
        SeasonNumber = 1,
        EpisodeNumber = 3
    };

    private static ProgramEntry Movie() => new(Guid.NewGuid(), "Heat", "overview", 1_000_000, "/media/movies/heat.mkv")
    {
        IsMovie = true,
        Year = 1995
    };

    [Fact]
    public void ParseProgramId_RoundTrips()
    {
        var parsed = RecordingService.ParseProgramId("livechannels-popular_639205414116214128");
        Assert.NotNull(parsed);
        Assert.Equal("livechannels-popular", parsed.Value.ChannelId);
        Assert.Equal(639205414116214128L, parsed.Value.StartTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nounderscore")]
    [InlineData("_123")]
    [InlineData("chan_")]
    [InlineData("chan_notticks")]
    public void ParseProgramId_RejectsMalformed(string? programId)
        => Assert.Null(RecordingService.ParseProgramId(programId));

    [Fact]
    public void RecordingName_UsesEpisodeNumbering()
        => Assert.Equal("Andor S01E03 Reckoning", RecordingService.RecordingNameFor(Episode(), DateTime.UtcNow));

    [Fact]
    public void RecordingName_UsesMovieYear()
        => Assert.Equal("Heat (1995)", RecordingService.RecordingNameFor(Movie(), DateTime.UtcNow));

    [Fact]
    public void BuildTarget_Episode_DefaultLayout()
    {
        var target = RecordingService.BuildTarget(FileSystem, options: null, Root, Episode(), DateTime.UtcNow, ".mkv");

        Assert.Equal(Path.Combine(Root, "Andor", "Season 1", "Andor S01E03 Reckoning.mkv"), target.File);
        Assert.Equal(Root, target.LibraryRoot);
        Assert.Equal("Recordings", target.LibraryName);
        Assert.Null(target.CollectionType);
    }

    [Fact]
    public void BuildTarget_HonorsSubfolderOption()
    {
        var options = new LiveTvOptions { EnableRecordingSubfolders = true };
        var target = RecordingService.BuildTarget(FileSystem, options, Root, Episode(), DateTime.UtcNow, ".mkv");

        Assert.Equal(Path.Combine(Root, "Series", "Andor", "Season 1", "Andor S01E03 Reckoning.mkv"), target.File);
    }

    [Fact]
    public void BuildTarget_UsesCustomSeriesPathAsItsOwnLibrary()
    {
        var options = new LiveTvOptions { SeriesRecordingPath = "/dvr/shows", EnableRecordingSubfolders = true };
        var target = RecordingService.BuildTarget(FileSystem, options, Root, Episode(), DateTime.UtcNow, ".mkv");

        // A distinct configured path gets no Series subfolder and registers as its own library, like the
        // built-in DVR's Recorded Shows.
        Assert.Equal(Path.Combine("/dvr/shows", "Andor", "Season 1", "Andor S01E03 Reckoning.mkv"), target.File);
        Assert.Equal("/dvr/shows", target.LibraryRoot);
        Assert.Equal("Recorded Shows", target.LibraryName);
        Assert.Equal(CollectionTypeOptions.tvshows, target.CollectionType);
    }

    [Fact]
    public void BuildTarget_Movie_GetsOwnFolder()
    {
        var target = RecordingService.BuildTarget(FileSystem, options: null, Root, Movie(), DateTime.UtcNow, ".mkv");

        Assert.Equal(Path.Combine(Root, "Heat (1995)", "Heat (1995).mkv"), target.File);
        Assert.Equal("Recordings", target.LibraryName);
    }

    [Fact]
    public void DateStamp_IsPlainLocalTime_OutsideTheFallBackHour()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var stamp = RecordingService.DateStamp(new DateTime(2026, 7, 23, 0, 30, 0, DateTimeKind.Utc), zone);
        Assert.Equal("2026_07_22_20_30_00", stamp);
    }

    [Fact]
    public void DateStamp_KeepsTheTwoFallBackAiringsApart()
    {
        // 01:30 happens twice in New York on 2026-11-01: first at UTC-4, then again an hour later at UTC-5.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var first = RecordingService.DateStamp(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), zone);
        var second = RecordingService.DateStamp(new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc), zone);

        Assert.NotEqual(first, second);
        Assert.StartsWith("2026_11_01_01_30_00", first, StringComparison.Ordinal);
        Assert.StartsWith("2026_11_01_01_30_00", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void RetryDelay_DoublesUpToTheCap(int attempts, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), RecordingService.RetryDelay(attempts));
    }

    [Fact]
    public void Hash_IsUnchangedForAscii_AndSeparatesNonLatinNames()
    {
        // The original byte-per-char FNV-1a, which existing ASCII logo file names and source ids were keyed by.
        static string Legacy(string value)
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash = unchecked((hash ^ (byte)c) * 16777619u);
            }

            return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(Legacy("Channel 7|2|tv|1"), LiveChannelsTvService.Hash("Channel 7|2|tv|1"));

        // U+0141 and U+0241 share a low byte, so the old hash could not tell these names apart.
        Assert.NotEqual(LiveChannelsTvService.Hash("Łodz"), LiveChannelsTvService.Hash("Ɂodz"));
    }
}
