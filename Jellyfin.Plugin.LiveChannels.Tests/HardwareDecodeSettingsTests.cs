using System;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests that the plugin decodes on the GPU exactly when Jellyfin's own hardware decoding settings say it may, so a
/// format the server's GPU cannot decode goes straight to software instead of failing on the GPU first; that the
/// tune-in card's last pass never decodes on the GPU; and that bumper renders wait for the item to settle.
/// </summary>
public class HardwareDecodeSettingsTests
{
    [Theory]
    [InlineData("h264", 8, "yuv420p", true)]
    [InlineData("vc1", 8, "yuv420p", true)]
    [InlineData("hevc", 8, "yuv420p", false)] // not ticked
    [InlineData("av1", 10, "yuv420p10le", false)] // not ticked
    [InlineData(null, null, null, true)] // unrecorded: keeps the GPU, as before
    public void JellyfinsDefaults_DecodeOnlyH264AndVc1OnTheGpu(string? codec, int? depth, string? format, bool expected)
        => Assert.Equal(expected, EncoderResolver.CanHardwareDecode(new EncodingOptions(), codec, depth, format));

    [Theory]
    [InlineData(8, "yuv420p", false, false, false, true)]
    [InlineData(10, "yuv420p10le", false, false, false, false)] // 10-bit HEVC needs its switch
    [InlineData(10, "yuv420p10le", true, false, false, true)]
    [InlineData(10, "yuv422p10le", true, false, false, false)] // range extension needs its own switch
    [InlineData(10, "yuv422p10le", true, true, false, true)]
    [InlineData(12, "yuv420p12le", true, true, false, false)] // 12-bit is range extension 12-bit
    [InlineData(12, "yuv444p12le", true, true, true, true)]
    [InlineData(8, "yuv444p", false, false, false, false)] // 8-bit 4:4:4 is range extension too
    public void Hevc_FollowsTheBitDepthAndRangeExtensionSwitches(int depth, string format, bool tenBit, bool rext10, bool rext12, bool expected)
    {
        var options = new EncodingOptions
        {
            HardwareDecodingCodecs = ["h264", "hevc"],
            EnableDecodingColorDepth10Hevc = tenBit,
            EnableDecodingColorDepth10HevcRext = rext10,
            EnableDecodingColorDepth12HevcRext = rext12,
        };

        Assert.Equal(expected, EncoderResolver.CanHardwareDecode(options, "hevc", depth, format));
    }

    [Fact]
    public void Vp9_TenBitNeedsItsSwitch()
    {
        var options = new EncodingOptions { HardwareDecodingCodecs = ["vp9"], EnableDecodingColorDepth10Vp9 = false };
        Assert.True(EncoderResolver.CanHardwareDecode(options, "vp9", 8, "yuv420p"));
        Assert.False(EncoderResolver.CanHardwareDecode(options, "vp9", 10, "yuv420p10le"));
        options.EnableDecodingColorDepth10Vp9 = true;
        Assert.True(EncoderResolver.CanHardwareDecode(options, "vp9", 10, "yuv420p10le"));
    }

    [Fact]
    public void AnItem_IsJudgedByWhatWasProbedForIt_AndAnHdrItemCountsAsTenBit()
    {
        var options = new EncodingOptions { HardwareDecodingCodecs = ["hevc"], EnableDecodingColorDepth10Hevc = false };
        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration("encoding").Returns(options);
        var resolver = new EncoderResolver(config, NullLogger<EncoderResolver>.Instance);

        Assert.True(resolver.CanHardwareDecode(Item("hevc", 8, "yuv420p")));
        Assert.False(resolver.CanHardwareDecode(Item("hevc", 10, "yuv420p10le")));

        // A schedule cached before depths were recorded still knows HDR is never 8-bit.
        Assert.False(resolver.CanHardwareDecode(new ProgramEntry(Guid.NewGuid(), "HDR", null, 1, "/x.mkv") { SourceVideoCodec = "hevc", IsHdr = true }));
        Assert.True(resolver.CanHardwareDecode(new ProgramEntry(Guid.NewGuid(), "Old", null, 1, "/x.mkv") { SourceVideoCodec = "hevc" }));
    }

    [Fact]
    public void TheTuneInCardsLastPass_DecodesInSoftware_AndEncodesOnTheGpu()
    {
        var qsv = new VideoEncoderProfile("h264_qsv", "H.264 (QSV)", true,
            new[] { "-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-init_hw_device", "qsv=hw@va", "-filter_hw_device", "hw" },
            Array.Empty<string>(), "format=nv12,hwupload=extra_hw_frames=64", false,
            DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: "hwdownload,format=nv12|p010le,", GpuDevice: "/dev/dri/renderD128");

        var args = IntroService.BuildEncodeArguments("/cache/card.src.mp4", 1920, 8000, qsv, "aac", 192, "/cache/card.ts");

        Assert.DoesNotContain("-hwaccel", args);
        Assert.DoesNotContain(args, a => a.Contains("scale_vaapi", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("hwupload", StringComparison.Ordinal));
        Assert.Equal("h264_qsv", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("-y", args[0]);
        Assert.Equal("/cache/card.ts", args[^1]);
    }

    [Theory]
    [InlineData(3600, 60)]
    [InlineData(240, 60)]
    [InlineData(120, 30)]
    [InlineData(20, 5)]
    [InlineData(0, 0)]
    public void ABumperRender_WaitsForTheItemToSettle_ButLeavesMostOfAShortItemToRender(double remainingSeconds, double expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BumperService.PrepareDelayFor(TimeSpan.FromSeconds(remainingSeconds)));

    private static ProgramEntry Item(string codec, int? depth, string? format)
        => new(Guid.NewGuid(), "Item", null, TimeSpan.FromMinutes(30).Ticks, "/x.mkv") { SourceVideoCodec = codec, SourceBitDepth = depth, SourcePixelFormat = format };
}
