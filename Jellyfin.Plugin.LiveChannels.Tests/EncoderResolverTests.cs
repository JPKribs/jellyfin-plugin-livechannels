using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="EncoderResolver"/> — the branchy mapping from Jellyfin's hardware-acceleration
/// configuration to a concrete ffmpeg encoder (a wrong mapping silently produces a black screen).
/// </summary>
public class EncoderResolverTests
{
    private static EncoderResolver Resolver(string? accel, bool enableHardware = true)
    {
        var options = new EncodingOptions { EnableHardwareEncoding = enableHardware };
        if (accel is not null)
        {
            // HardwareAccelerationType may be an enum or a string across versions; set it either way.
            var prop = typeof(EncodingOptions).GetProperty("HardwareAccelerationType")!;
            prop.SetValue(options, prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, accel, true) : accel);
        }

        var config = Substitute.For<IServerConfigurationManager>();
        config.GetConfiguration(Arg.Any<string>()).Returns(options);
        return new EncoderResolver(config, Substitute.For<ILogger<EncoderResolver>>());
    }

    [Theory]
    [InlineData("videotoolbox", VideoCodec.H264, "h264_videotoolbox")]
    [InlineData("videotoolbox", VideoCodec.Hevc, "hevc_videotoolbox")]
    [InlineData("nvenc", VideoCodec.H264, "h264_nvenc")]
    [InlineData("amf", VideoCodec.Hevc, "hevc_amf")]
    [InlineData("qsv", VideoCodec.H264, "h264_qsv")]
    [InlineData("vaapi", VideoCodec.H264, "h264_vaapi")]
    public void ResolveVideo_MapsHardwareAccel_ToEncoder(string accel, VideoCodec codec, string expected)
    {
        var profile = Resolver(accel).ResolveVideo(codec, allowHardware: true);
        Assert.Equal(expected, profile.Name);
        Assert.True(profile.IsHardware);
        Assert.False(profile.UsePreset); // hardware encoders skip the libx264/265 -preset
    }

    [Fact]
    public void ResolveVideo_NoAccel_UsesSoftwareLibx264()
    {
        var profile = Resolver("none").ResolveVideo(VideoCodec.H264, allowHardware: true);
        Assert.Equal("libx264", profile.Name);
        Assert.False(profile.IsHardware);
        Assert.True(profile.UsePreset);
    }

    [Fact]
    public void ResolveVideo_HevcSoftware_UsesLibx265()
        => Assert.Equal("libx265", Resolver("none").ResolveVideo(VideoCodec.Hevc, allowHardware: true).Name);

    [Fact]
    public void ResolveVideo_DisallowHardware_ForcesSoftware_EvenWithAccelConfigured()
    {
        var profile = Resolver("videotoolbox").ResolveVideo(VideoCodec.H264, allowHardware: false);
        Assert.Equal("libx264", profile.Name);
        Assert.False(profile.IsHardware);
    }

    [Fact]
    public void ResolveVideo_HardwareEncodingDisabledInJellyfin_UsesSoftware()
        => Assert.Equal("libx264", Resolver("videotoolbox", enableHardware: false).ResolveVideo(VideoCodec.H264, true).Name);

    [Fact]
    public void ResolveVideo_VideoToolbox_EnablesHardwareDecode()
        => Assert.Equal("videotoolbox", Resolver("videotoolbox").ResolveVideo(VideoCodec.H264, true).DecodeHwaccel);

    [Fact]
    public void ResolveVideo_Nvenc_UsesCudaDecode()
        => Assert.Equal("cuda", Resolver("nvenc").ResolveVideo(VideoCodec.H264, true).DecodeHwaccel);

    [Fact]
    public void ResolveVideo_Qsv_UsesQsvDecode()
        => Assert.Equal("qsv", Resolver("qsv").ResolveVideo(VideoCodec.H264, true).DecodeHwaccel);

    [Fact]
    public void ResolveVideo_Software_HasNoHardwareDecode()
        => Assert.Null(Resolver("none").ResolveVideo(VideoCodec.H264, true).DecodeHwaccel);

    [Fact]
    public void ResolveVideo_Vaapi_AddsDeviceInit_AndHardwareUpload()
    {
        var profile = Resolver("vaapi").ResolveVideo(VideoCodec.H264, allowHardware: true);
        Assert.Contains("-init_hw_device", profile.InitArgs);
        Assert.Contains("hwupload", profile.PixelStage);
    }

    [Theory]
    [InlineData(AudioCodec.Aac, "aac", 192)]
    [InlineData(AudioCodec.Ac3, "ac3", 256)]
    [InlineData(AudioCodec.Eac3, "eac3", 256)]
    public void ResolveAudio_MapsCodec_AndBitrate(AudioCodec codec, string encoder, int bitrate)
    {
        var (e, b) = EncoderResolver.ResolveAudio(codec);
        Assert.Equal(encoder, e);
        Assert.Equal(bitrate, b);
    }
}
