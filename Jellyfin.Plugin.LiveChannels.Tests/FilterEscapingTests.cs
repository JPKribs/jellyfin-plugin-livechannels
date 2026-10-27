using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="StreamArguments.QuoteFilterValue"/>: file paths inside a <c>-filter_complex</c> graph are
/// unescaped twice by ffmpeg (graph, then filter option), so a path with an apostrophe (<c>Bob's Show</c>) broke
/// the whole graph ("Error parsing filterchain") and the item never played.
/// </summary>
public class FilterEscapingTests
{
    private static readonly VideoEncoderProfile SoftwareH264 = new(
        "libx264", "H.264 (software)", false, Array.Empty<string>(), Array.Empty<string>(), "format=yuv420p", true);

    private static readonly VideoEncoderProfile QsvLinux = new(
        "h264_qsv", "QSV", true, Array.Empty<string>(), Array.Empty<string>(), "format=nv12,hwupload", false,
        DecodeHwaccel: "qsv", DecodeOutputFormat: "qsv", DecodeDownload: "hwdownload,format=nv12|p010le,",
        GpuDevice: "/dev/dri/renderD128");

    [Theory]
    [InlineData("/m.mkv", "'/m.mkv'")]
    [InlineData("/tv/Bob's Show/S01E01.mkv", @"'/tv/Bob\'\''s Show/S01E01.mkv'")]
    [InlineData(@"C:\media\a.mkv", @"'C\:\\media\\a.mkv'")]
    [InlineData("/a, [b]; c.mkv", "'/a, [b]; c.mkv'")]
    public void QuoteFilterValue_EscapesForBothParsers(string raw, string expected)
    {
        Assert.Equal(expected, StreamArguments.QuoteFilterValue(raw));
    }

    [Fact]
    public void ApostropheInMediaPath_NeverLeavesABareQuoteInsideTheQuotedSpan()
    {
        // The old escaping emitted \' INSIDE a single-quoted span, where the graph parser takes the quote as the
        // end of the span: everything after it was parsed as graph syntax.
        var a = StreamArguments.Build("/tv/Bob's Show/S01E01.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, (0, true));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains(@"subtitles=filename='/tv/Bob\'\''s Show/S01E01.mkv':si=0", fc, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Bob\'s", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void ApostropheInExtractedSubtitleAndFontsDir_IsEscapedToo()
    {
        var a = StreamArguments.Build(
            "/m.mkv", default, default, 1280, 4000, SoftwareH264, "aac", 192, (0, true), "/cache/Bob's/sub.ass",
            false, false, null, 0, null, "BorderStyle=3", "/cache/Bob's/fonts");
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains(@"filename='/cache/Bob\'\''s/sub.ass'", fc, StringComparison.Ordinal);
        Assert.Contains(@":fontsdir='/cache/Bob\'\''s/fonts'", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void ApostropheInGpuBurnInPath_IsEscaped()
    {
        var a = StreamArguments.Build("/tv/Bob's Show/S01E01.mkv", default, default, 1920, 16000, QsvLinux, "aac", 192, (1, true));
        var fc = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains(@"[bg]subtitles=filename='/tv/Bob\'\''s Show/S01E01.mkv':si=1:alpha=1", fc, StringComparison.Ordinal);
    }

    [Fact]
    public void IntroCaptionFontPath_UsesTheSameEscaping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lc-esc-" + Guid.NewGuid().ToString("N"), "Bob's fonts");
        Directory.CreateDirectory(dir);
        var font = Path.Combine(dir, "a.ttf");
        var logo = Path.Combine(dir, "logo.png");
        File.WriteAllBytes(font, new byte[] { 0 });
        File.WriteAllBytes(logo, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        try
        {
            var strips = new LineStrips(Path.Combine(Path.GetTempPath(), "lc-card"), 1280, 720);
            var joined = string.Join(" ", IntroService.BuildSourceArguments(logo, strips, "/tmp/out.mp4", font));
            Assert.Contains("drawtext=fontfile=" + StreamArguments.QuoteFilterValue(font) + ":text=", joined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("/fonts/Bob's Fonts/Sans.ttf")]
    [InlineData(@"C:\Windows\Fonts\arial.ttf")]
    public void SlateAndLogoFonts_UseTheSameEscaping(string font)
    {
        // These two drawtext call sites used to wrap the raw path in quotes (with backslashes turned into
        // slashes), so an apostrophe ended the quoted span and a drive colon split the option.
        var quoted = StreamArguments.QuoteFilterValue(font);
        var vf = string.Join(" ", StreamArguments.BuildSlate(1280, 4000, 10, TimeSpan.Zero, font));
        Assert.Contains("drawtext=fontfile=" + quoted + ":text='Standby'", vf, StringComparison.Ordinal);
        Assert.Contains("drawtext=fontfile=" + quoted + ":text='Channel content is unavailable'", vf, StringComparison.Ordinal);
        Assert.Equal("drawtext=fontfile=" + quoted + ":text='5'", DefaultLogoService.DrawText(font, "5"));
    }

    [Fact]
    public void RealFfmpeg_ParsesAWindowsStylePath()
    {
        // A file literally named like a Windows path (drive colon and backslashes) exercises both escapes the
        // drawtext font paths rely on. The local build has no drawtext, so the shared escaping is proven through
        // the movie filter, which parses its filename option the same way.
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "lc-esc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var media = Path.Combine(root, @"C:\Fonts\Bob's.mkv");
        try
        {
            Assert.Equal(0, Run(ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=64x48:d=1", "-c:v", "mpeg4", media));
            Assert.Equal(0, Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "movie=filename=" + StreamArguments.QuoteFilterValue(media) + ",scale=32:-2", "-frames:v", "1", "-f", "null", "-"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RealFfmpeg_ParsesTheEscapedPath()
    {
        // Proves the escaping against ffmpeg's actual parsers, not just a string shape: the `movie` source filter
        // takes a filename option exactly like `subtitles` does and is in every build (libass is not). Skipped
        // quietly where no ffmpeg is installed.
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "lc-esc-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, @"Bob's Show, [x] a:b\c; d");
        Directory.CreateDirectory(dir);
        var media = Path.Combine(dir, "S01E01.mkv");
        try
        {
            Assert.Equal(0, Run(ffmpeg, "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=64x48:d=1", "-c:v", "mpeg4", media));
            Assert.Equal(0, Run(ffmpeg, "-v", "error", "-filter_complex", "movie=filename=" + StreamArguments.QuoteFilterValue(media) + ",scale=32:-2[v]", "-map", "[v]", "-frames:v", "1", "-f", "null", "-"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindFfmpeg()
    {
        // jellyfin-ffmpeg first: it is what production runs, and what CI provides (where a missing one fails).
        if (FfmpegHarness.Instance is { } harness)
        {
            return harness.Ffmpeg;
        }

        var candidates = new List<string> { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, "ffmpeg"));
        }

        return candidates.Find(File.Exists);
    }

    private static int Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
