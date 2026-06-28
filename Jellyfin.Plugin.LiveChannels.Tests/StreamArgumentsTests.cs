using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="StreamArguments"/> — the per-item ffmpeg argument shapes that broke repeatedly.
/// </summary>
public class StreamArgumentsTests
{
    private static readonly VideoEncoderProfile SoftwareH264 = new(
        "libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private static List<string> Build(TimeSpan offset = default, TimeSpan timeline = default, (int, bool)? sub = null)
        => StreamArguments.Build("/m.mkv", offset, timeline, 1280, 4000, SoftwareH264, "aac", 192, sub);

    private static List<string> Build(VideoEncoderProfile video, string audioEncoder = "aac", int audioBitrate = 192)
        => StreamArguments.Build("/m.mkv", default, default, 1280, 4000, video, audioEncoder, audioBitrate, null);

    // True when the flag is immediately followed by the value (an ffmpeg flag/value pair).
    private static bool Pair(List<string> a, string flag, string value)
    {
        for (var i = 0; i < a.Count - 1; i++)
        {
            if (a[i] == flag && a[i + 1] == value)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void MapsDefaultAudioTrack_ByOrdinal()
    {
        // Honor the audio track Jellyfin marks as default (here the third audio stream) instead of letting ffmpeg
        // pick by channel count. Any -map disables ffmpeg's auto-selection, so the video is mapped explicitly too.
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false, false, 2);
        Assert.Contains("0:a:2?", a);
        Assert.Contains("0:v:0", a);
    }

    [Fact]
    public void MapsFirstAudio_WhenNoDefaultOrdinal()
    {
        // With no ordinal supplied the first audio track is mapped, optional so an item without audio still runs.
        Assert.Contains("0:a:0?", Build());
    }

    [Fact]
    public void SignalsProgressiveFieldOrder_OnBothPaths()
    {
        // The encoder must tag the output progressive so Jellyfin remuxes instead of adding a deinterlace pass
        // that fails on QSV. The per-frame setparams flag is not honored by every hardware encoder, so the
        // field order is also set on the encoder itself.
        Assert.True(Pair(Build(), "-field_order", "progressive"));
        Assert.True(Pair(StreamArguments.BuildConcat("/tmp/list.txt", default, 1280, 4000, SoftwareH264, "aac", 192), "-field_order", "progressive"));
    }

    // The Intel pipeline, built the Tunarr/ErsatzTV way: VAAPI decode + GPU filters + VAAPI encode, no hwdownload.
    private static readonly VideoEncoderProfile VaapiStyle = new(
        "h264_vaapi", "VAAPI", true,
        new[] { "-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va" },
        Array.Empty<string>(), "format=nv12,hwupload", false,
        DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: "", VaapiFilters: true);

    [Fact]
    public void VaapiPipeline_ScalesOnGpu_NeverDownloads()
    {
        // The whole point of the VAAPI pipeline: frames stay on the GPU. scale_vaapi/pad_vaapi do the work and
        // there is NO hwdownload, the step that crashed the old path on 10-bit (p010) sources (exit 234).
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, VaapiStyle, "aac", 192, null);
        Assert.True(Pair(a, "-hwaccel", "vaapi"));
        Assert.True(Pair(a, "-hwaccel_output_format", "vaapi"));
        Assert.True(a.IndexOf("-hwaccel") < a.IndexOf("-i"));
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("scale_vaapi=w=1280:h=720:format=nv12", vf, StringComparison.Ordinal);
        Assert.Contains("pad_vaapi=w=1280:h=720:x=-1:y=-1:color=black", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("hwdownload", vf, StringComparison.Ordinal);
        Assert.True(Pair(a, "-c:v", "h264_vaapi"));
    }

    [Fact]
    public void VaapiPipeline_Interlaced_DeinterlacesOnGpu_ProgressiveSkips()
    {
        // Interlaced sources deinterlace on the GPU; progressive ones skip it (deinterlacing progressive halves
        // quality). Either way, never a hwdownload.
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, VaapiStyle, "aac", 192, null, null, false, false, isInterlaced: true);
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.StartsWith("deinterlace_vaapi,", vf);
        Assert.DoesNotContain("hwdownload", vf, StringComparison.Ordinal);

        var prog = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, VaapiStyle, "aac", 192, null);
        Assert.DoesNotContain("deinterlace_vaapi", prog[prog.IndexOf("-vf") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void VaapiPipeline_SoftwareOverride_UploadsForEncoder()
    {
        // The per-item fallback decodes in software, so the GPU filter chain can't run; it uploads the system
        // frames for the VAAPI encoder instead. Hardware ENCODE is still used.
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, VaapiStyle, "aac", 192, null, null, softwareDecode: true);
        Assert.DoesNotContain("-hwaccel", a);
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.DoesNotContain("scale_vaapi", vf, StringComparison.Ordinal);
        Assert.Contains("hwupload", vf, StringComparison.Ordinal);
        Assert.True(Pair(a, "-c:v", "h264_vaapi"));
    }

    [Fact]
    public void Hdr_OnVaapi_TonemapsOnGpu_NoDownload_NoHwmap()
    {
        // HDR tone-maps on the GPU with tonemap_vaapi, then scales/pads on the GPU and encodes directly: no
        // hwdownload, no hwmap-to-QSV, no software zscale.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 8000, VaapiStyle, "aac", 192, null, null, false, isHdr: true);
        Assert.True(Pair(a, "-hwaccel", "vaapi"));
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709", vf, StringComparison.Ordinal);
        Assert.Contains("scale_vaapi=w=1920:h=1080", vf, StringComparison.Ordinal);
        Assert.Contains("pad_vaapi=w=1920:h=1080:x=-1:y=-1", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("hwdownload", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("hwmap", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("zscale", vf, StringComparison.Ordinal);
        Assert.True(Pair(a, "-c:v", "h264_vaapi"));
        Assert.True(Pair(a, "-field_order", "progressive"));
    }

    [Fact]
    public void Hdr_SoftwareFallback_UsesSoftwareTonemap_NotVaapi()
    {
        // The per-item fallback (softwareDecode) drops HDR back to the software zscale tone-map, no VAAPI.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 8000, VaapiStyle, "aac", 192, null, null, softwareDecode: true, isHdr: true);
        Assert.DoesNotContain("-hwaccel", a);
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("tonemap=tonemap=hable", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap_vaapi", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void AlwaysTranscodes_Libx264_8bit_ConstantScale_NeverCopies()
    {
        var a = Build();
        Assert.True(Pair(a, "-c:v", "libx264"));
        Assert.False(Pair(a, "-c:v", "copy"));
        Assert.Contains(a, x => x.Contains("scale=1280:720") && x.Contains("fps=30") && x.Contains("format=yuv420p"));
        Assert.True(Pair(a, "-preset", "veryfast")); // software encoder uses a preset
    }

    [Fact]
    public void HardwareProfile_AddsInitArgs_PixelUpload_AndNoPreset()
    {
        var vaapi = new VideoEncoderProfile("h264_vaapi", "H.264 (VAAPI)", true,
            new[] { "-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va" },
            Array.Empty<string>(), "format=nv12,hwupload", false);
        var a = Build(vaapi);
        Assert.True(Pair(a, "-c:v", "h264_vaapi"));
        Assert.True(Pair(a, "-init_hw_device", "vaapi=va:/dev/dri/renderD128"));
        Assert.Contains(a, x => x.Contains("hwupload"));
        Assert.False(Pair(a, "-preset", "veryfast")); // hardware encoder skips the libx264 preset
        Assert.True(a.IndexOf("-init_hw_device") < a.IndexOf("-i")); // device init before input
    }

    [Fact]
    public void HardwareDecode_AddsHwaccelBeforeInput()
    {
        var vt = new VideoEncoderProfile("h264_videotoolbox", "vt", true, Array.Empty<string>(),
            Array.Empty<string>(), "format=yuv420p", false, "videotoolbox");
        var a = Build(vt);
        Assert.True(Pair(a, "-hwaccel", "videotoolbox"));
        Assert.True(a.IndexOf("-hwaccel") < a.IndexOf("-i"));
    }

    [Fact]
    public void SoftwareDecode_OmitsHwaccel()
        => Assert.DoesNotContain("-hwaccel", Build());

    [Fact]
    public void Concat_LoopsPlaylist_SeeksIn_NoSeamFlags()
    {
        var a = StreamArguments.BuildConcat("/tmp/list.txt", TimeSpan.FromSeconds(30), 1280, 4000, SoftwareH264, "aac", 192);
        Assert.True(Pair(a, "-stream_loop", "-1"));
        Assert.True(Pair(a, "-f", "concat"));
        Assert.True(Pair(a, "-i", "/tmp/list.txt"));
        Assert.True(Pair(a, "-ss", "30.000"));
        Assert.Contains(a, x => x.Contains("scale=1280:720") && x.Contains("fps=30"));
        Assert.True(Pair(a, "-c:v", "libx264"));
        Assert.DoesNotContain("-hwaccel", a);            // software decode for the concat pipeline
        Assert.DoesNotContain("-output_ts_offset", a);   // one continuous encoder, no per-item offset
        Assert.Contains("+initial_discontinuity", a);    // flags the self-heal restart seam on the file path
        Assert.Equal("pipe:1", a[^1]);
    }

    [Fact]
    public void Hdr_InsertsTonemapChain_OnlyWhenHdr()
    {
        // SDR: no tone-map. HDR: the zscale->tonemap->zscale chain is inserted after deinterlace, before scale.
        var sdr = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false);
        Assert.DoesNotContain(sdr, x => x.Contains("tonemap", StringComparison.Ordinal));

        var hdr = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, isHdr: true);
        Assert.Contains(hdr, x => x.Contains("yadif=deint=1,zscale=t=linear", StringComparison.Ordinal)
            && x.Contains("tonemap=tonemap=hable", StringComparison.Ordinal)
            && x.Contains("zscale=t=bt709", StringComparison.Ordinal)
            && x.IndexOf("tonemap", StringComparison.Ordinal) < x.IndexOf("scale=1280:720", StringComparison.Ordinal));
    }

    [Fact]
    public void AudioEncoder_AndBitrate_AreUsed()
    {
        var a = Build(SoftwareH264, "eac3", 256);
        Assert.True(Pair(a, "-c:a", "eac3"));
        Assert.True(Pair(a, "-b:a", "256k"));
    }

    [Fact]
    public void Robustness_GenPts_AudioResample_AndMuxQueue()
    {
        var a = Build();
        Assert.True(Pair(a, "-fflags", "+genpts"));
        Assert.Contains(a, x => x.StartsWith("aresample=async=1", StringComparison.Ordinal));
        Assert.True(Pair(a, "-max_muxing_queue_size", "1024"));
    }

    [Fact]
    public void BurnIn_TextSubtitle_UsesSubtitlesFilter()
    {
        var a = Build(sub: (0, true));
        Assert.Contains("-filter_complex", a);
        Assert.Contains(a, x => x.Contains("subtitles=") && x.Contains(":si=0"));
    }

    [Fact]
    public void BitmapSubtitle_UsesOverlayFilter()
    {
        var a = Build(sub: (2, false));
        Assert.Contains(a, x => x.Contains("[0:s:2]overlay"));
    }

    [Fact]
    public void Offset_UsesInputSeek_WhenNotBurningSubtitles()
    {
        var a = Build(offset: TimeSpan.FromSeconds(30));
        Assert.True(a.IndexOf("-ss") >= 0 && a.IndexOf("-ss") < a.IndexOf("-i")); // before -i
    }

    [Fact]
    public void Offset_UsesInputSeekWithCopyts_WhenBurningSubtitles()
    {
        var a = Build(offset: TimeSpan.FromSeconds(30), sub: (0, true));
        Assert.True(a.IndexOf("-ss") >= 0 && a.IndexOf("-ss") < a.IndexOf("-i")); // input seek (before -i): fast
        Assert.Contains("-copyts", a); // keeps source timestamps so the burned subtitle stays aligned
    }

    [Fact]
    public void Timeline_AddsOutputTsOffset_OnlyWhenPositive()
    {
        Assert.True(Pair(Build(timeline: TimeSpan.FromSeconds(100)), "-output_ts_offset", "100.000"));
        Assert.DoesNotContain("-output_ts_offset", Build());
    }

    [Fact]
    public void Output_IsAlwaysMpegtsToPipe()
    {
        var a = Build();
        Assert.True(Pair(a, "-f", "mpegts"));
        Assert.Equal("pipe:1", a[^1]);
    }

    [Fact]
    public void Slate_FallsBackToColourBars_WithoutAFont()
    {
        var a = StreamArguments.BuildSlate(1280, 4000, 10, TimeSpan.Zero, null);
        Assert.Contains(a, x => x.Contains("smptebars=size=1280x720"));
        Assert.DoesNotContain(a, x => x.Contains("drawtext"));
        Assert.True(Pair(a, "-c:v", "libx264"));
        Assert.True(Pair(a, "-f", "mpegts"));
        Assert.Equal("pipe:1", a[^1]);
    }

    [Fact]
    public void Slate_LabelsStandby_WhenAFontIsAvailable()
    {
        var a = StreamArguments.BuildSlate(1280, 4000, 10, TimeSpan.Zero, "/fonts/Sans.ttf");
        Assert.Contains(a, x => x.Contains("drawtext") && x.Contains("Standby"));
        Assert.Contains(a, x => x.Contains("Channel content is unavailable"));
        Assert.DoesNotContain(a, x => x.Contains("smptebars"));
    }
}
