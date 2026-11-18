using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Copying an item's video into the channel's stream instead of encoding it.
/// </summary>
public class CopyVideoTests
{
    private static ProgramEntry Item(string? codec, int height = 1080, int? depth = 8, string? pixelFormat = "yuv420p", bool hdr = false, int rotation = 0, bool videoOnly = false)
        => new(Guid.NewGuid(), "Item", null, TimeSpan.FromMinutes(30).Ticks, "/media/item.mkv")
        {
            SourceVideoCodec = codec,
            SourceHeight = height,
            SourceBitDepth = depth,
            SourcePixelFormat = pixelFormat,
            IsHdr = hdr,
            SourceRotation = rotation,
            IsVideoOnly = videoOnly,
        };

    [Theory]
    [InlineData("h264", VideoCodec.H264, true)]
    [InlineData("H264", VideoCodec.H264, true)]
    [InlineData("h264", VideoCodec.Hevc, false)]
    [InlineData("hevc", VideoCodec.Hevc, true)]
    [InlineData("hevc", VideoCodec.H264, false)]
    [InlineData("av1", VideoCodec.Hevc, false)]
    [InlineData("mpeg2video", VideoCodec.H264, false)]
    [InlineData(null, VideoCodec.H264, false)]
    public void CanCopyVideo_TakesOnlyTheChannelsCodec(string? codec, VideoCodec channel, bool expected)
        => Assert.Equal(expected, StreamArguments.CanCopyVideo(Item(codec), channel, 1080));

    [Fact]
    public void CanCopyVideo_LeavesOutWhatTheChannelMustChange()
    {
        // Larger than the channel, HDR, turned, not 4:2:0, too deep, or with no audio to carry.
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", height: 2160), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("hevc", depth: 10, pixelFormat: "yuv420p10le", hdr: true), VideoCodec.Hevc, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", rotation: 90), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", pixelFormat: "yuv422p"), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", pixelFormat: null), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", depth: 10, pixelFormat: "yuv420p10le"), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", videoOnly: true), VideoCodec.H264, 1080));
        Assert.False(StreamArguments.CanCopyVideo(Item("h264", height: 0), VideoCodec.H264, 1080));

        // Smaller than the channel, and 10 bit SDR HEVC, are copied.
        Assert.True(StreamArguments.CanCopyVideo(Item("h264", height: 480), VideoCodec.H264, 1080));
        Assert.True(StreamArguments.CanCopyVideo(Item("hevc", depth: 10, pixelFormat: "yuv420p10le"), VideoCodec.Hevc, 1080));
    }

    [Fact]
    public void BuildCopy_CopiesTheVideoAndEncodesTheAudioOntoTheTimeline()
    {
        var args = string.Join(' ', StreamArguments.BuildCopy("/media/item.mkv", TimeSpan.FromSeconds(90.5), TimeSpan.FromSeconds(600), "aac", 192, 1, 30, TimeSpan.FromMinutes(5)));

        Assert.Equal(
            "-hide_banner -loglevel error -progress pipe:2 -fflags +genpts -noaccurate_seek -ss 90.700 -t 300.000 -readrate 1.0 -readrate_initial_burst 30 "
            + "-i /media/item.mkv -map 0:v:0 -map 0:a:1? -c:v copy -c:a aac -b:a 192k -ac 2 -ar 48000 -af aresample=async=1:min_hard_comp=0.100 -max_muxing_queue_size 1024 "
            + "-output_ts_offset 600.200 -mpegts_flags +initial_discontinuity -f mpegts -muxpreload 0 -muxdelay 0 pipe:1",
            args);
    }

    [Fact]
    public void KeyframeBefore_TakesTheLastKeyframeAtOrBeforeThePoint()
    {
        var output = "440.440000,K__\n440.482000,___\n444.444000,K__\n448.448900,K__\n448.490000,___\n452.452000,K__\n";

        // Rounded down to the millisecond, so a seek to it never lands past the keyframe.
        Assert.Equal(TimeSpan.FromMilliseconds(448448), StreamArguments.KeyframeBefore(output, TimeSpan.FromSeconds(449.012)));
        Assert.Equal(TimeSpan.FromMilliseconds(444444), StreamArguments.KeyframeBefore(output, TimeSpan.FromSeconds(444.444)));
        Assert.Null(StreamArguments.KeyframeBefore(output, TimeSpan.FromSeconds(100)));
        Assert.Null(StreamArguments.KeyframeBefore(string.Empty, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void BuildKeyframeProbe_ReadsOnlyTheStretchBeforeThePoint()
    {
        Assert.Contains("434.012%449.013", StreamArguments.BuildKeyframeProbe("/media/item.mkv", TimeSpan.FromSeconds(449.012)));
        Assert.Contains("0.000%5.001", StreamArguments.BuildKeyframeProbe("/media/item.mkv", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BuildCopy_FromTheStartOfTheStream_HasNoSeekOrOffset()
    {
        var args = StreamArguments.BuildCopy("/media/item.mkv", TimeSpan.Zero, TimeSpan.Zero, "ac3", 256, null, 0);

        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-output_ts_offset", args);
        Assert.DoesNotContain("-readrate_initial_burst", args);
        Assert.Contains("-c:v", args);
    }
}
