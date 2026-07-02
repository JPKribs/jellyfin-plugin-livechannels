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
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false, 2);
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
    public void OnScheduleItems_PaceAtExactlyRealtime_WithNoBurst()
    {
        // The read rate is fixed at exactly realtime on every path (a higher rate would advance the live edge
        // faster than the player can follow, forever). An item started with no accumulated deficit gets NO burst:
        // an unearned burst would lurch the live edge forward for no reason.
        var a = Build();
        Assert.True(Pair(a, "-readrate", "1.0"));
        Assert.DoesNotContain("-readrate_initial_burst", a);
        var concat = StreamArguments.BuildConcat("/tmp/list.txt", default, 1280, 4000, SoftwareH264, "aac", 192);
        Assert.True(Pair(concat, "-readrate", "1.0"));
    }

    [Fact]
    public void FirstItem_BurstsATuneInHeadStart()
    {
        // The first item of a session fills the head start before the player has tuned in, so its burst is safe.
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false, null, StreamArguments.InitialBurstSeconds);
        Assert.True(Pair(a, "-readrate_initial_burst", "30"));
    }

    [Fact]
    public void DeficitBurst_IsEmittedInSeconds_ThreeDecimals()
    {
        // A later item bursts exactly the session's accumulated wall-clock deficit, formatted like the read rate.
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false, null, 2.5);
        Assert.True(Pair(a, "-readrate_initial_burst", "2.5"));
    }

    [Theory]
    [InlineData(0, 0, 30)]      // session start: the deficit IS the full head start
    [InlineData(100, 130, 0)]   // exactly on schedule (elapsed + head start == produced): no burst
    [InlineData(100, 125, 5)]   // 5s lost at boundaries so far: burst exactly that
    [InlineData(500, 400, 30)]  // a large backlog is healed at most one head start per boundary
    [InlineData(10, 45, 0)]     // ahead of schedule (warm splices): never burst further forward
    public void BurstForDeficit_TracksTheSessionWallClock(double elapsedSeconds, double timelineSeconds, double expected)
        => Assert.Equal(expected, StreamArguments.BurstForDeficit(TimeSpan.FromSeconds(elapsedSeconds), TimeSpan.FromSeconds(timelineSeconds)), 3);

    [Fact]
    public void SignalsProgressiveFieldOrder_OnBothPaths()
    {
        // The encoder must tag the output progressive so Jellyfin remuxes instead of adding a deinterlace pass
        // that fails on QSV. The per-frame setparams flag is not honored by every hardware encoder, so the
        // field order is also set on the encoder itself.
        Assert.True(Pair(Build(), "-field_order", "progressive"));
        Assert.True(Pair(StreamArguments.BuildConcat("/tmp/list.txt", default, 1280, 4000, SoftwareH264, "aac", 192), "-field_order", "progressive"));
    }

    private static readonly VideoEncoderProfile QsvStyle = new(
        "h264_qsv", "QSV", true, Array.Empty<string>(), Array.Empty<string>(), "format=nv12,hwupload", false,
        DecodeHwaccel: "qsv", DecodeOutputFormat: "qsv", DecodeDownload: "hwdownload,format=nv12,");

    // A Linux Intel profile with a known render node: takes the fully GPU-resident pipeline.
    private static readonly VideoEncoderProfile QsvLinux = new(
        "h264_qsv", "QSV", true, Array.Empty<string>(), Array.Empty<string>(), "format=nv12,hwupload", false,
        DecodeHwaccel: "qsv", DecodeOutputFormat: "qsv", DecodeDownload: "hwdownload,format=nv12|p010le,",
        GpuDevice: "/dev/dri/renderD128");

    private static readonly VideoEncoderProfile VaapiLinux = new(
        "h264_vaapi", "VAAPI", true, Array.Empty<string>(), Array.Empty<string>(), "format=nv12,hwupload", false,
        DecodeHwaccel: "vaapi", DecodeOutputFormat: "vaapi", DecodeDownload: "hwdownload,format=nv12|p010le,",
        GpuDevice: "/dev/dri/renderD128");

    [Fact]
    public void IntelGpuPipeline_Sdr_StaysInVramEndToEnd()
    {
        // The benchmarked STEP 3 graph: VAAPI decode on Jellyfin's configured render node (never vendor_id
        // matching), GPU deinterlace/scale/pad (p010->nv12 conversion happens ON the GPU, killing the 10-bit
        // software-decode workaround), then hwmap into the QSV encoder. No hwdownload, no hwupload, no CPU scale.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, null);
        Assert.True(Pair(a, "-init_hw_device", "vaapi=va:/dev/dri/renderD128"));
        Assert.True(Pair(a, "-init_hw_device", "qsv=qs@va"));
        Assert.True(Pair(a, "-hwaccel", "vaapi"));
        Assert.True(Pair(a, "-hwaccel_output_format", "vaapi"));
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("deinterlace_vaapi=rate=frame:auto=1", vf, StringComparison.Ordinal);
        Assert.Contains("scale_vaapi=w=1920:h=1080:force_original_aspect_ratio=decrease:format=nv12", vf, StringComparison.Ordinal);
        Assert.Contains("hwmap=derive_device=qsv,format=qsv", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("hwdownload", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("hwupload", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor_id", string.Join(' ', a), StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_Hdr_ScalesFirst_ToneMaps_PadsLast()
    {
        // Scale at p010 on the GPU first (tone map runs on the small frames), and pad LAST: letterbox bars
        // painted before the tone map sit in PQ/bt2020 space and the LUT renders them green instead of black.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, null, null, false, isHdr: true);
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("scale_vaapi=w=1920:h=1080:force_original_aspect_ratio=decrease:format=p010", vf, StringComparison.Ordinal);
        Assert.Contains("tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709", vf, StringComparison.Ordinal);
        Assert.True(vf.IndexOf("scale_vaapi", StringComparison.Ordinal) < vf.IndexOf("tonemap_vaapi", StringComparison.Ordinal));
        Assert.True(vf.IndexOf("tonemap_vaapi", StringComparison.Ordinal) < vf.IndexOf("pad_vaapi", StringComparison.Ordinal));
        Assert.Contains("hwmap=derive_device=qsv,format=qsv", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("zscale", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("procamp_vaapi", vf, StringComparison.Ordinal); // neutral gain adds no stage
    }

    [Fact]
    public void IntelGpuPipeline_Hdr_AppliesJellyfinVppBrightness_BeforeThePad()
    {
        // Jellyfin's VPP tone-mapping brightness gain rides the profile; the procamp stage sits between the
        // tone map and the pad so the gain lifts the picture without greying the letterbox bars.
        var bright = QsvLinux with { VppBrightness = 16, VppContrast = 1 };
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, bright, "aac", 192, null, null, false, isHdr: true);
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.Contains("tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709,procamp_vaapi=b=16,pad_vaapi", vf, StringComparison.Ordinal);

        // Contrast joins the same stage only when non-neutral, and SDR content gets no procamp at all.
        var contrast = QsvLinux with { VppBrightness = 16, VppContrast = 1.2 };
        var b = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, contrast, "aac", 192, null, null, false, isHdr: true);
        Assert.Contains("procamp_vaapi=b=16:c=1.2,", b[b.IndexOf("-vf") + 1], StringComparison.Ordinal);
        var sdr = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, bright, "aac", 192, null);
        Assert.DoesNotContain("procamp_vaapi", sdr[sdr.IndexOf("-vf") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_VaapiEncoder_NeedsNoQsvMap()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, VaapiLinux, "aac", 192, null);
        Assert.True(Pair(a, "-c:v", "h264_vaapi"));
        Assert.True(Pair(a, "-filter_hw_device", "va"));
        var vf = a[a.IndexOf("-vf") + 1];
        Assert.DoesNotContain("hwmap", vf, StringComparison.Ordinal);
        Assert.Contains("scale_vaapi", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_TextBurnIn_CompositesOnGpu()
    {
        // Burn-in stays GPU-resident: libass renders the subtitle onto a transparent alphasrc canvas on the
        // CPU, hwupload lifts just that picture into VRAM, and overlay_vaapi composites it over the untouched
        // GPU frames before the QSV hand-off. The filter device must be the VAAPI one (hwupload targets it).
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (1, true));
        Assert.True(Pair(a, "-filter_hw_device", "va"));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale_vaapi=w=1920:h=1080", fc, StringComparison.Ordinal);
        Assert.Contains("alphasrc=s=1920x1080:r=10:start=0", fc, StringComparison.Ordinal);
        Assert.Contains("subtitles=filename='/m.mkv':si=1:alpha=1", fc, StringComparison.Ordinal);
        Assert.Contains("hwupload[sub]", fc, StringComparison.Ordinal);
        Assert.Contains("[main][sub]overlay_vaapi=eof_action=pass", fc, StringComparison.Ordinal);
        Assert.Contains("hwmap=derive_device=qsv,format=qsv[v]", fc, StringComparison.Ordinal);
        Assert.DoesNotContain("hwdownload", fc, StringComparison.Ordinal);
        Assert.True(Pair(a, "-map", "[v]"));
    }

    [Fact]
    public void IntelGpuPipeline_TextBurnIn_DeepSeek_AlignsAlphasrcWithCopyts()
    {
        // On a deep tune-in the -copyts seek leaves the video PTS at the offset; the alphasrc canvas must start
        // there too or every subtitle renders minutes early.
        var a = StreamArguments.Build("/m.mkv", TimeSpan.FromSeconds(90), default, 1920, 16000, QsvLinux, "aac", 192, (0, true));
        Assert.Contains("-copyts", a);
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("alphasrc=s=1920x1080:r=10:start=90.000", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_ExternalSubtitle_ReadsTheExtractedFile()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (2, true), "/cache/sub.ass");
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("subtitles=filename='/cache/sub.ass':alpha=1", fc, StringComparison.Ordinal);
        Assert.DoesNotContain(":si=", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_BitmapBurnIn_UploadsTheSubtitleStream()
    {
        // PGS/VOBSUB: the sparse sub2video frames scale to the output size on the CPU, upload, and composite.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (0, false));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:s:0]scale=1920:1080[subsw];[subsw]hwupload[sub]", fc, StringComparison.Ordinal);
        Assert.Contains("overlay_vaapi=eof_action=pass", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_HdrBurnIn_KeepsGpuToneMap()
    {
        // The exact case the Forced-only escalation used to break: HDR 4K with a burned subtitle previously
        // dropped to the ~1.2x software chain; now the scale-first VPP tone map runs in the main GPU chain.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (0, true), null, false, isHdr: true);
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("tonemap_vaapi=format=nv12:t=bt709:m=bt709:p=bt709", fc, StringComparison.Ordinal);
        Assert.Contains("overlay_vaapi", fc, StringComparison.Ordinal);
        Assert.DoesNotContain("zscale", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_VaapiEncoderBurnIn_HasNoQsvTail()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, VaapiLinux, "aac", 192, (0, true));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("overlay_vaapi=eof_action=pass[v]", fc, StringComparison.Ordinal);
        Assert.DoesNotContain("hwmap", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntelGpuPipeline_SoftwareRetry_StaysOffTheGpu()
    {
        // The software retry must not touch the GPU (it exists to survive a broken hardware decode).
        var retry = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, null, null, softwareDecode: true);
        Assert.DoesNotContain("-hwaccel", retry);
        Assert.DoesNotContain(retry, x => x.Contains("scale_vaapi", StringComparison.Ordinal));
        Assert.True(Pair(retry, "-c:v", "h264_qsv")); // hardware ENCODE still applies via hwupload

        // And a burn-in retry uses the CPU subtitle chain, not overlay_vaapi.
        var burnRetry = StreamArguments.Build("/m.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (0, true), null, softwareDecode: true);
        var fc = burnRetry[burnRetry.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("overlay_vaapi", fc, StringComparison.Ordinal);
        Assert.Contains("subtitles=", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareDecode_GpuResident_DownloadsBeforeScale_AndSetsOutputFormat()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, QsvStyle, "aac", 192, null);
        Assert.True(Pair(a, "-hwaccel", "qsv"));
        Assert.True(Pair(a, "-hwaccel_output_format", "qsv"));
        Assert.True(a.IndexOf("-hwaccel") < a.IndexOf("-i"));
        Assert.Contains(a, x => x.StartsWith("hwdownload,format=nv12,", StringComparison.Ordinal) && x.Contains("scale=1280:720", StringComparison.Ordinal));
    }

    [Fact]
    public void SoftwareDecodeOverride_OmitsHardwareDecode()
    {
        var a = StreamArguments.Build("/m.mkv", default, default, 1280, 4000, QsvStyle, "aac", 192, null, null, softwareDecode: true);
        Assert.DoesNotContain("-hwaccel", a);
        Assert.DoesNotContain("-hwaccel_output_format", a);
        Assert.DoesNotContain(a, x => x.Contains("hwdownload", StringComparison.Ordinal));
        Assert.True(Pair(a, "-c:v", "h264_qsv")); // hardware ENCODE is still used
    }

    [Fact]
    public void Concat_HardwareDecode_DownloadsBeforeScale()
    {
        var a = StreamArguments.BuildConcat("/tmp/list.txt", default, 1280, 4000, QsvStyle, "aac", 192, decodeHwaccel: "qsv");
        Assert.True(Pair(a, "-hwaccel", "qsv"));
        Assert.True(Pair(a, "-hwaccel_output_format", "qsv"));
        Assert.Contains(a, x => x.StartsWith("hwdownload,format=nv12,", StringComparison.Ordinal));
    }

    [Fact]
    public void Hdr_OnIntelWithoutRenderNode_UsesNoVaapiGraph()
    {
        // Intel without a known render node has no GPU-resident graph; HDR keeps the profile's own init (no
        // vendor-matched VAAPI device) so the caller's software route handles the tone map.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 8000, QsvStyle, "aac", 192, null, null, false, isHdr: true);
        Assert.DoesNotContain(a, x => x.Contains("vendor_id", StringComparison.Ordinal));
        Assert.DoesNotContain(a, x => x.Contains("tonemap_vaapi", StringComparison.Ordinal));
    }

    [Fact]
    public void Hdr_SoftwareFallback_UsesSoftwareTonemap_NotVaapi()
    {
        // The per-item fallback (softwareDecode) drops HDR back to the software zscale tone-map, no VAAPI.
        var a = StreamArguments.Build("/m.mkv", default, default, 1920, 8000, QsvStyle, "aac", 192, null, null, softwareDecode: true, isHdr: true);
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
    public void DurationLimit_BoundsTheInput_ForTheStressTest()
    {
        // The stress test runs the production command over a fixed slice: -t is an input option (before -i)
        // and appears only when a limit is passed — live channels never set one.
        var bounded = StreamArguments.Build("/m.mkv", TimeSpan.FromSeconds(300), default, 1280, 4000, SoftwareH264, "aac", 192, null, null, false, false, null, 0, TimeSpan.FromSeconds(60));
        Assert.True(Pair(bounded, "-t", "60"));
        Assert.True(bounded.IndexOf("-t") < bounded.IndexOf("-i"));
        Assert.DoesNotContain("-t", Build());
    }

    [Fact]
    public void Segmenter_WritesThePlaylistAtomically()
    {
        // temp_file makes playlist rewrites atomic (write to .tmp, then rename) so a reader polling at the live
        // edge can never catch a truncated in-place write, which fails its reload and crashes its HLS demuxer.
        var a = StreamArguments.BuildHlsSegmenter("/s/seg%d.ts", "/s/stream.m3u8", 75);
        Assert.Contains(a, x => x.Contains("temp_file", StringComparison.Ordinal));
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
