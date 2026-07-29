using System;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="LiveChannelsTvService.NextSegmentNumber"/>: when a session's encoder dies while someone
/// is still watching, the replacement writes into the same directory, so it has to continue the numbering rather
/// than overwrite segments the viewer is still working through.
/// </summary>
public sealed class ProducerRestartTests : IDisposable
{
    private readonly string _dir;

    public ProducerRestartTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lc-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSegment(long number) => File.WriteAllText(Path.Combine(_dir, "seg" + number + ".ts"), "x");

    [Fact]
    public void EmptyDirectory_StartsAtZero()
        => Assert.Equal(0, LiveChannelsTvService.NextSegmentNumber(_dir));

    [Fact]
    public void ContinuesPastTheNewestSegment()
    {
        WriteSegment(0);
        WriteSegment(1);
        WriteSegment(2);

        Assert.Equal(3, LiveChannelsTvService.NextSegmentNumber(_dir));
    }

    [Fact]
    public void UsesTheHighestNumber_NotTheCount()
    {
        // The rolling window deletes old segments, so the surviving ones do not start at zero.
        WriteSegment(418);
        WriteSegment(419);
        WriteSegment(420);

        Assert.Equal(421, LiveChannelsTvService.NextSegmentNumber(_dir));
    }

    [Fact]
    public void IgnoresFilesThatAreNotSegments()
    {
        WriteSegment(5);
        File.WriteAllText(Path.Combine(_dir, "stream.m3u8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.log"), "log");
        File.WriteAllText(Path.Combine(_dir, "segwhat.ts"), "x");

        Assert.Equal(6, LiveChannelsTvService.NextSegmentNumber(_dir));
    }

    [Fact]
    public void MissingDirectory_StartsAtZero()
        => Assert.Equal(0, LiveChannelsTvService.NextSegmentNumber(Path.Combine(_dir, "gone")));
}
