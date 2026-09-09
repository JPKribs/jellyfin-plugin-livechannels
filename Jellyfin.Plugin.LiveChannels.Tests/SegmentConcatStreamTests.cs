using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="SegmentConcatStream"/> — the reader that serves a session's rolling HLS segments back
/// to Jellyfin's internal live stream endpoint as one continuous MPEG-TS byte stream.
/// </summary>
public sealed class SegmentConcatStreamTests : IDisposable
{
    private readonly string _dir;

    public SegmentConcatStreamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lc-concat-" + Guid.NewGuid().ToString("N"));
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

    private void WriteSegment(long number, string content)
        => File.WriteAllText(Path.Combine(_dir, "seg" + number + ".ts"), content);

    private static string ReadAll(Stream stream)
    {
        // Read until the stream reports the live edge (a zero-length read).
        using var buffer = new MemoryStream();
        var chunk = new byte[16];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [Fact]
    public void ReadsConsecutiveSegmentsAsOneStream()
    {
        WriteSegment(0, "AAA");
        WriteSegment(1, "BBB");
        WriteSegment(2, "CCC");

        using var stream = new SegmentConcatStream(_dir);
        Assert.Equal("AAABBBCCC", ReadAll(stream));
    }

    [Fact]
    public void StartsTwoSegmentsBehindTheNewest()
    {
        // A long-running session: the reader must join near the live edge, not at the oldest segment.
        for (var i = 10; i <= 20; i++)
        {
            WriteSegment(i, "s" + i + "|");
        }

        using var stream = new SegmentConcatStream(_dir);
        Assert.Equal("s18|s19|s20|", ReadAll(stream));
    }

    [Fact]
    public void StartBehindWidensTheOpeningCushion()
    {
        // A fresh session's readers take the whole backlog (the initial burst), clamped to what exists.
        for (var i = 0; i <= 6; i++)
        {
            WriteSegment(i, "s" + i + "|");
        }

        using var stream = new SegmentConcatStream(_dir, log: null, startBehind: 8);
        Assert.Equal("s0|s1|s2|s3|s4|s5|s6|", ReadAll(stream));
    }

    [Fact]
    public void HoldBehindWithholdsTheNewestSegments()
    {
        for (var i = 0; i <= 9; i++)
        {
            WriteSegment(i, "s" + i + "|");
        }

        using var stream = new SegmentConcatStream(_dir, log: null, startBehind: 20, holdBehind: () => 3);

        // seg7, seg8, and seg9 are the producer's reserve; the reader stops at seg6.
        Assert.Equal("s0|s1|s2|s3|s4|s5|s6|", ReadAll(stream));

        // A new segment moves the held-back edge forward by one.
        WriteSegment(10, "s10|");
        Assert.Equal("s7|", ReadAll(stream));
    }

    [Fact]
    public void HoldBehindIsConsultedLiveSoTheReserveCanGrowMidStream()
    {
        for (var i = 0; i <= 5; i++)
        {
            WriteSegment(i, "s" + i + "|");
        }

        // Starts with no reserve (a brand-new session serves everything it has), then the reserve is raised.
        var hold = 0;
        using var stream = new SegmentConcatStream(_dir, log: null, startBehind: 20, holdBehind: () => hold);
        Assert.Equal("s0|s1|s2|s3|s4|s5|", ReadAll(stream));

        hold = 2;
        WriteSegment(6, "s6|");
        WriteSegment(7, "s7|");
        WriteSegment(8, "s8|");

        // seg7 and seg8 are now the reserve.
        Assert.Equal("s6|", ReadAll(stream));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]
    [InlineData(30, 1)]
    [InlineData(45, 2)]
    [InlineData(60, 4)]
    [InlineData(3600, 4)]
    public void HoldBehindRampsInOneSegmentPerStepUpToTheFullReserve(int ageSeconds, int expected)
        => Assert.Equal(expected, DirectLiveStream.HoldBehindFor(TimeSpan.FromSeconds(ageSeconds)));

    [Fact]
    public void FreshReadersStartOnTheWholeBurst()
    {
        // A reader joining a fresh session must start at least the full initial burst behind the edge, so the
        // tune-in position (the oldest segment) is where playback begins, and an established reader must start
        // ahead of the hold-back so it always has something servable.
        Assert.True(DirectLiveStream.FreshStartBehind * StreamArguments.SegmentSeconds >= StreamArguments.InitialBurstSeconds + IntroService.IntroSeconds);
        Assert.True(DirectLiveStream.FreshStartBehind > DirectLiveStream.HoldBehind);
    }

    [Fact]
    public void HoldBehindClampsOnYoungSessionsSoTheOldestSegmentIsServable()
    {
        // A brand-new session (probe time) has fewer segments than the hold-back; the oldest must still serve.
        WriteSegment(0, "AAA");
        WriteSegment(1, "BBB");

        using var stream = new SegmentConcatStream(_dir, log: null, startBehind: 8, holdBehind: () => 3);
        Assert.Equal("AAA", ReadAll(stream));
    }

    [Fact]
    public void ReturnsZeroAtTheLiveEdgeThenResumesWhenTheNextSegmentAppears()
    {
        WriteSegment(0, "AAA");

        using var stream = new SegmentConcatStream(_dir);
        Assert.Equal("AAA", ReadAll(stream));

        // At the edge: nothing more yet.
        var buffer = new byte[16];
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));

        // The segmenter finishes the next segment; the same reader picks it up.
        WriteSegment(1, "BBB");
        Assert.Equal("BBB", ReadAll(stream));
    }

    [Fact]
    public void SkipsForwardWhenTheWindowDeletedTheNextSegment()
    {
        WriteSegment(0, "AAA");

        using var stream = new SegmentConcatStream(_dir);
        Assert.Equal("AAA", ReadAll(stream));

        // seg1 aged out before the reader got there; it must jump to the oldest survivor instead of stalling.
        WriteSegment(4, "EEE");
        WriteSegment(5, "FFF");
        Assert.Equal("EEEFFF", ReadAll(stream));
    }

    [Fact]
    public void ReturnsZeroWhenTheDirectoryIsEmptyOrGone()
    {
        using (var stream = new SegmentConcatStream(_dir))
        {
            var buffer = new byte[16];
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        }

        Directory.Delete(_dir, recursive: true);
        using (var stream = new SegmentConcatStream(_dir))
        {
            var buffer = new byte[16];
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        }

        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void IgnoresTempAndForeignFiles()
    {
        WriteSegment(0, "AAA");
        File.WriteAllText(Path.Combine(_dir, "seg1.ts.tmp"), "PARTIAL");
        File.WriteAllText(Path.Combine(_dir, "segther.ts"), "JUNK");
        File.WriteAllText(Path.Combine(_dir, "stream.m3u8"), "#EXTM3U");

        using var stream = new SegmentConcatStream(_dir);
        Assert.Equal("AAA", ReadAll(stream));
    }

    [Fact]
    public void IsReadOnlyAndUnseekable()
    {
        using var stream = new SegmentConcatStream(_dir);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(Array.Empty<byte>(), 0, 0));
    }
}
