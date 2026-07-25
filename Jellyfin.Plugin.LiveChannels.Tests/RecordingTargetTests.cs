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
}
