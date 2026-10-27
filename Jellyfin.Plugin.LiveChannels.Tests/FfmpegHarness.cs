using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Runs the plugin's real ffmpeg argument lists against a real jellyfin-ffmpeg and measures the output, so a change
/// to a filter chain is proven by what comes out (geometry, scan type, colour, audio layout, timestamps) rather than
/// by the shape of an argument string. Only jellyfin-ffmpeg is used, because the production chains rely on its
/// filters; set <c>LIVECHANNELS_TEST_FFMPEG</c> to point at one, otherwise the usual install locations are tried.
/// Tests skip quietly (return early) when none is found, matching the rest of the suite.
/// </summary>
internal sealed class FfmpegHarness
{
    private static readonly Lazy<FfmpegHarness?> Shared = new(Locate);

    private FfmpegHarness(string ffmpeg, string ffprobe)
    {
        Ffmpeg = ffmpeg;
        Ffprobe = ffprobe;
    }

    /// <summary>Gets the jellyfin-ffmpeg harness, or <c>null</c> when this machine has none.</summary>
    public static FfmpegHarness? Instance => Shared.Value;

    public string Ffmpeg { get; }

    public string Ffprobe { get; }

    /// <summary>Runs a process to completion, returning its exit code and both output streams.</summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> args, string? stdinFile = null, int timeoutSeconds = 120)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinFile is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdinFile is not null)
        {
            using (var input = File.OpenRead(stdinFile))
            {
                try
                {
                    input.CopyTo(process.StandardInput.BaseStream);
                }
                catch (IOException)
                {
                    // The process stopped reading early; its exit code and stderr say why.
                }
            }

            process.StandardInput.Close();
        }

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(file + " did not finish within " + timeoutSeconds + "s: " + string.Join(' ', info.ArgumentList));
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// The RGB a player shows at one point of a frame: the pixels around it are averaged and converted from the
    /// given matrix and range (what the player read from the stream's colour description).
    /// </summary>
    public (int R, int G, int B) Rgb(string path, double atSeconds, int x, int y, string matrix = "bt709", string range = "tv")
    {
        var info = new ProcessStartInfo(Ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
        {
            "-v", "error", "-i", path, "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-frames:v", "1",
            "-vf", $"crop=8:8:{x - 4}:{y - 4},setparams=range={range}:colorspace={(matrix == "bt601" ? "smpte170m" : matrix)},scale=1:1:out_range=pc:flags=area,format=rgb24",
            "-f", "rawvideo", "-"
        })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        var rgb = buffer.ToArray();
        Assert(rgb.Length >= 3, "no pixel read from " + path);
        return (rgb[0], rgb[1], rgb[2]);
    }

    /// <summary>Runs ffmpeg and fails with its stderr when it exits non-zero.</summary>
    public void Exec(params string[] args)
    {
        // Every caller runs at -v error, so any stderr at all is a failure: ffmpeg can report a fatal filter error
        // mid-stream and still exit 0 with a truncated file.
        var (code, _, err) = Run(Ffmpeg, args);
        if (code != 0 || !string.IsNullOrWhiteSpace(err))
        {
            throw new InvalidOperationException("ffmpeg exited " + code + ": " + err + "\n$ ffmpeg " + string.Join(' ', args));
        }
    }

    /// <summary>
    /// Runs a producer argument list (as built by StreamArguments) with its <c>pipe:1</c> output redirected to a
    /// file, optionally capping the output length (the concat producer loops forever).
    /// </summary>
    public string RunProducer(IReadOnlyList<string> args, string outputPath, double? outputSeconds = null)
    {
        Assert(args[^1] == "pipe:1", "a producer writes to pipe:1");
        var list = args.Take(args.Count - 1).ToList();
        if (outputSeconds is { } limit)
        {
            list.Add("-t");
            list.Add(limit.ToString("0.###", CultureInfo.InvariantCulture));
        }

        list.Add(outputPath);
        var (code, _, err) = Run(Ffmpeg, list);
        if (code != 0)
        {
            throw new InvalidOperationException("producer exited " + code + ": " + Tail(err) + "\n$ ffmpeg " + string.Join(' ', list));
        }

        return err;
    }

    /// <summary>Gets ffprobe's stream list for a file.</summary>
    public IReadOnlyList<JsonElement> Streams(string path)
    {
        var (code, json, err) = Run(Ffprobe, new[] { "-v", "error", "-show_streams", "-of", "json", path });
        Assert(code == 0, "ffprobe failed: " + err);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>Gets the first stream of a type (<c>video</c>/<c>audio</c>), or <c>null</c>.</summary>
    public JsonElement? Stream(string path, string codecType)
    {
        foreach (var s in Streams(path))
        {
            if (s.GetProperty("codec_type").GetString() == codecType)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>Decodes a whole file and returns whatever ffmpeg reported as an error (empty when clean).</summary>
    public string DecodeErrors(string path)
    {
        var (_, _, err) = Run(Ffmpeg, new[] { "-v", "error", "-i", path, "-f", "null", "-" });
        return err.Trim();
    }

    /// <summary>
    /// Measures the picture area that is not letterbox/pillarbox black, via cropdetect over a few frames from
    /// <paramref name="atSeconds"/>. Fixtures that use this are flat mid-grey, so the active area is exact.
    /// </summary>
    public (int Width, int Height) ActiveArea(string path, double atSeconds = 1)
    {
        var (_, _, err) = Run(Ffmpeg, new[]
        {
            "-hide_banner", "-i", path, "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vf", "cropdetect=limit=24:round=2:reset=0", "-frames:v", "10", "-f", "null", "-"
        });
        var matches = Regex.Matches(err, @"crop=(\d+):(\d+):\d+:\d+");
        Assert(matches.Count > 0, "cropdetect reported nothing: " + Tail(err));
        var last = matches[^1];
        return (int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Counts interlaced vs progressive frames via idet's multi-frame detection.</summary>
    public (int Interlaced, int Progressive) Idet(string path)
    {
        var (_, _, err) = Run(Ffmpeg, new[] { "-hide_banner", "-i", path, "-vf", "idet", "-f", "null", "-" });
        // ffmpeg can print an empty summary for an idle filter instance first; the last one is the real count.
        var all = Regex.Matches(err, @"Multi frame detection: TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)");
        Assert(all.Count > 0, "idet reported nothing: " + Tail(err));
        var m = all[^1];
        int N(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return (N(1) + N(2), N(3));
    }

    /// <summary>The mean luma (0-255) of a file's frames from signalstats.</summary>
    public double MeanLuma(string path)
    {
        var (_, _, err) = Run(Ffmpeg, new[]
        {
            "-hide_banner", "-i", path, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"
        });
        var values = Regex.Matches(err, @"lavfi\.signalstats\.YAVG=([\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert(values.Count > 0, "signalstats reported nothing: " + Tail(err));
        return values.Average();
    }

    /// <summary>Extracts one frame to a PNG.</summary>
    public string Frame(string path, string pngPath, double atSeconds = 0, string? filter = null)
    {
        // Output seeking (decode from the start and discard) so the reference is exact and a TS input never starts
        // decoding on a B-frame whose references were skipped; the fixtures are seconds long.
        var args = new List<string> { "-v", "error", "-y", "-i", path };
        if (atSeconds > 0)
        {
            args.Add("-ss");
            args.Add(atSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        if (filter is not null)
        {
            args.Add("-vf");
            args.Add(filter);
        }

        args.AddRange(new[] { "-frames:v", "1", pngPath });
        Exec(args.ToArray());
        return pngPath;
    }

    /// <summary>The SSIM (0-1) between two same-sized images.</summary>
    public double Ssim(string a, string b)
    {
        var (_, _, err) = Run(Ffmpeg, new[] { "-hide_banner", "-i", a, "-i", b, "-lavfi", "ssim", "-f", "null", "-" });
        var m = Regex.Match(err, @"All:([\d.]+)");
        Assert(m.Success, "ssim reported nothing: " + Tail(err));
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Every packet of the first stream of a type, as (pts, dts, duration) seconds in file order.</summary>
    public IReadOnlyList<(double Pts, double Dts, double Duration)> Packets(string path, string selector)
    {
        var (code, csv, err) = Run(Ffprobe, new[]
        {
            "-v", "error", "-select_streams", selector, "-show_entries", "packet=pts_time,dts_time,duration_time", "-of", "csv=p=0", path
        });
        Assert(code == 0, "ffprobe packets failed: " + err);
        var list = new List<(double, double, double)>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3 || !TryParse(parts[0], out var pts) || !TryParse(parts[1], out var dts))
            {
                continue;
            }

            TryParse(parts[2], out var dur);
            list.Add((pts, dts, dur));
        }

        return list;
    }

    /// <summary>The mean audio volume in dB of a stretch of a file (about -91 for digital silence).</summary>
    public double MeanVolume(string path, double fromSeconds, double seconds)
    {
        var (_, _, err) = Run(Ffmpeg, new[]
        {
            "-hide_banner", "-i", path, "-ss", fromSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-vn", "-af", "volumedetect", "-f", "null", "-"
        });
        var m = Regex.Match(err, @"mean_volume: (-?[\d.]+) dB");
        Assert(m.Success, "volumedetect reported nothing: " + Tail(err));
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Measures picture/sound sync on a sync clip's output: the time of every white flash in the picture, the start
    /// of every beep in the sound, and for each flash the offset to its beep (positive: sound late), in seconds.
    /// </summary>
    public (IReadOnlyList<double> Flashes, IReadOnlyList<double> Beeps, IReadOnlyList<double> Offsets) Sync(string path)
    {
        // -copyts: measure on the stream's real timestamps, as a player does. Without it ffmpeg re-bases a
        // transport stream's audio to start at zero when decoding, which hides a real start delay between the
        // tracks and reports a desync that is not in the stream.
        var (_, _, video) = Run(Ffmpeg, new[] { "-hide_banner", "-copyts", "-i", path, "-an", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-" });
        var flashes = new List<double>();
        double? t = null;
        var lit = false;
        foreach (var line in video.Split('\n'))
        {
            var pts = Regex.Match(line, @"pts_time:([-\d.]+)");
            if (pts.Success)
            {
                t = double.Parse(pts.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var y = Regex.Match(line, @"YAVG=([\d.]+)");
            if (y.Success && t is { } now)
            {
                var on = double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) > 100;
                if (on && !lit)
                {
                    flashes.Add(now);
                }

                lit = on;
            }
        }

        var (_, _, audio) = Run(Ffmpeg, new[] { "-hide_banner", "-copyts", "-i", path, "-vn", "-af", "silencedetect=noise=-30dB:d=0.01", "-f", "null", "-" });
        var beeps = Regex.Matches(audio, @"silence_end: ([-\d.]+)").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var offsets = new List<double>();
        foreach (var flash in flashes)
        {
            var near = beeps.Where(b => Math.Abs(b - flash) < 0.5).ToList();
            if (near.Count > 0)
            {
                offsets.Add(near.OrderBy(b => Math.Abs(b - flash)).First() - flash);
            }
        }

        return (flashes, beeps, offsets);
    }

    /// <summary>Whether this ffmpeg has the named filter.</summary>
    public bool HasFilter(string name)
    {
        var (_, output, _) = Run(Ffmpeg, new[] { "-hide_banner", "-filters" });
        return Regex.IsMatch(output, @"^\s*\S+\s+" + Regex.Escape(name) + @"\s", RegexOptions.Multiline);
    }

    /// <summary>Whether this ffmpeg has the named encoder.</summary>
    public bool HasEncoder(string name)
    {
        var (_, output, _) = Run(Ffmpeg, new[] { "-hide_banner", "-encoders" });
        return Regex.IsMatch(output, @"^\s*\S+\s+" + Regex.Escape(name) + @"\s", RegexOptions.Multiline);
    }

    internal static string Tail(string text, int max = 2000)
        => text.Length <= max ? text : text[^max..];

    private static bool TryParse(string s, out double value)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static FfmpegHarness? Locate()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("LIVECHANNELS_TEST_FFMPEG");
        if (!string.IsNullOrEmpty(env))
        {
            candidates.Add(env);
        }

        candidates.Add("/usr/lib/jellyfin-ffmpeg/ffmpeg");
        candidates.Add("/usr/share/jellyfin-ffmpeg/ffmpeg");
        candidates.Add("/Applications/Jellyfin.app/Contents/MacOS/ffmpeg");
        candidates.Add(@"C:\Program Files\Jellyfin\Server\ffmpeg.exe");

        foreach (var ffmpeg in candidates)
        {
            if (!File.Exists(ffmpeg))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(ffmpeg)!;
            var ffprobe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(ffprobe))
            {
                return new FfmpegHarness(ffmpeg, ffprobe);
            }
        }

        return null;
    }
}

/// <summary>
/// Synthetic media generated once per test run with lavfi, each file built to carry exactly the property a test
/// exists for (anamorphic pixels, interlacing, PQ HDR, no audio, 5.1, a short audio track, a text subtitle).
/// Geometry fixtures are flat mid-grey so cropdetect measures the active picture exactly.
/// </summary>
public sealed class MediaFixtures : IDisposable
{
    private readonly Dictionary<string, Lazy<string>> _files = new();

    public MediaFixtures()
    {
        Root = Path.Combine(Path.GetTempPath(), "lc-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        var ff = FfmpegHarness.Instance;
        if (ff is null)
        {
            return;
        }

        void Define(string name, Func<string, string[]> args)
            => _files[name] = new Lazy<string>(() =>
            {
                var path = Path.Combine(Root, name);
                var list = new List<string> { "-v", "error", "-y" };
                list.AddRange(args(path));
                list.Add(path);
                ff.Exec(list.ToArray());
                return path;
            });

        const string Sine = "sine=frequency=1000:sample_rate=48000";
        Define("progressive_1080p.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30:d=6", "-f", "lavfi", "-i", Sine + ":d=6",
            "-ac", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        Define("grey_4x3.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=640x480:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        // A 16:9 DVD: 720x480 stored pixels with a 32:27 sample aspect ratio.
        Define("grey_anamorphic_dvd.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=720x480:r=30:d=4,setsar=32/27", "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        Define("grey_16x9.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=1920x1080:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        // A 1.85:1 scope picture.
        Define("grey_scope.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=1998x1080:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        // A source that does not signal its sample aspect ratio at all (0:1), which must be read as square.
        Define("grey_sar_unknown.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=720x480:r=30:d=4,setsar=0", "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        Define("interlaced_1080i.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=60:d=4,tinterlace=mode=interleave_top,setfield=tff",
            "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-flags", "+ildct+ilme", "-x264opts", "tff=1",
            "-c:a", "aac", "-shortest"
        });
        Define("hdr10_pq.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=4,format=yuv420p,zscale=tin=bt709:min=bt709:pin=bt709:rin=tv:t=smpte2084:m=bt2020nc:p=bt2020:r=tv,format=yuv420p10le",
            "-f", "lavfi", "-i", Sine + ":d=4",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "tv",
            "-c:a", "aac", "-shortest"
        });
        Define("video_only.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=4", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"
        });
        Define("surround_51.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4",
            "-af", "pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        // Five seconds of picture over three seconds of sound.
        Define("short_audio.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=5", "-f", "lavfi", "-i", Sine + ":d=3",
            "-ac", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac"
        });
        // No -shortest here: it drops the lone subtitle packet, leaving an empty track.
        Define("text_subtitle.mkv", path =>
        {
            var srt = Path.ChangeExtension(path, ".srt");
            File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:10,000\nSUBTITLE BURN-IN CHECK\n");
            return new[]
            {
                "-f", "lavfi", "-i", "color=c=0x808080:s=1280x720:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4", "-i", srt,
                "-map", "0:v", "-map", "1:a", "-map", "2:s",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt"
            };
        });
        Define("video_only_subtitle.mkv", path =>
        {
            var srt = Path.ChangeExtension(path, ".srt");
            File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:10,000\nSUBTITLE BURN-IN CHECK\n");
            return new[]
            {
                "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=5", "-i", srt, "-map", "0:v", "-map", "1:s",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:s", "srt"
            };
        });
        // A second stereo AAC H.264 item at a different picture size and sample rate, which the concat path can
        // follow (only codec and channel changes break it).
        Define("stereo_44k.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=c=0x808080:s=640x480:r=25:d=4", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:d=4",
            "-ac", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"
        });
        // A default stereo track with a tone, then a second, non-default 5.1 track of silence: playing the second
        // one by mistake is audible as silence.
        Define("two_track.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=4", "-f", "lavfi", "-i", Sine + ":d=4", "-f", "lavfi", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-ac:a:0", "2", "-t", "4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-disposition:a:0", "default", "-disposition:a:1", "0"
        });
        // Sync clips: a one-frame white flash and a 30 ms 1 kHz beep at the same instants (1 s, 3 s, 5 s, 7 s, 9 s),
        // so the offset between picture and sound can be measured anywhere downstream.
        string Flash(string fps) => "drawbox=x=0:y=0:w=iw:h=ih:c=white:t=fill:enable='between(mod(t\\,2)\\,1\\,1+0.5/" + fps + ")'";
        string Beep(double shift) => "aevalsrc='if(between(mod(t+" + shift.ToString(CultureInfo.InvariantCulture) + "\\,2)\\,1\\,1.03)\\,sin(2*PI*1000*t)\\,0)':s=48000:d=10";
        Define("sync_30.mkv", _ => new[] { "-f", "lavfi", "-i", "color=black:s=1920x1080:r=30:d=10," + Flash("30"), "-f", "lavfi", "-i", Beep(0), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2" });
        Define("sync_2398.mkv", _ => new[] { "-f", "lavfi", "-i", "color=black:s=1920x1080:r=24000/1001:d=10," + Flash("24"), "-f", "lavfi", "-i", Beep(0), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2" });
        Define("sync_25i_ac3.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=black:s=720x576:r=50:d=10," + Flash("50") + ",tinterlace=mode=interleave_top,setfield=tff", "-f", "lavfi", "-i", Beep(0),
            "-af", "pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-flags", "+ildct+ilme", "-c:a", "ac3"
        });
        // The audio track starts 0.3 s after the picture in the container, as many real files do, with its beeps
        // placed so that in presentation time they still land on the flashes: that intended offset must survive.
        Define("sync_audio_delayed.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=black:s=1920x1080:r=30:d=10," + Flash("30"), "-itsoffset", "0.3", "-f", "lavfi", "-i", Beep(0.3),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2"
        });
        // A DVR-style transport stream whose timestamps start at 10 s rather than zero, with a broadcast GOP (a
        // keyframe every half second): a seek into a transport stream starts the picture at the next keyframe.
        Define("sync_ts_start10.ts", _ => new[]
        {
            "-f", "lavfi", "-i", "color=black:s=1920x1080:r=30000/1001:d=10," + Flash("30"), "-f", "lavfi", "-i", Beep(0),
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "15", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-output_ts_offset", "10", "-f", "mpegts"
        });
        Define("sync_subs.mkv", path =>
        {
            var srt = Path.ChangeExtension(path, ".srt");
            File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:10,000\nSYNC\n");
            return new[]
            {
                "-f", "lavfi", "-i", "color=black:s=1920x1080:r=30:d=10," + Flash("30"), "-f", "lavfi", "-i", Beep(0), "-i", srt,
                "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", "-c:s", "srt"
            };
        });
        Define("sync_hdr10.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "color=black:s=1920x1080:r=24:d=10," + Flash("24") + ",format=yuv420p,zscale=tin=bt709:min=bt709:pin=bt709:rin=tv:t=smpte2084:m=bt2020nc:p=bt2020:r=tv:npl=203,format=yuv420p10le",
            "-f", "lavfi", "-i", Beep(0), "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error",
            "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-c:a", "aac", "-ac", "2"
        });
        // Six bars of known sRGB colour (red, green, blue, white, mid grey, a skin tone) in every colour format a
        // library holds: HD bt709, an SD bt601 DVD, untagged SD and HD (read by players as bt601 and bt709), a
        // full-range HD recording, and HDR10 (PQ, bt2020, 10-bit, with SDR white at the 203-nit reference level of
        // BT.2408, where HDR mastering puts diffuse white).
        const string Bars = "color=c=0xFF0000:s=160x480,format=rgb24[a];color=c=0x00FF00:s=160x480[b];color=c=0x0000FF:s=160x480[c];"
            + "color=c=0xFFFFFF:s=160x480[d];color=c=0x808080:s=160x480[e];color=c=0xDCA078:s=160x480[f];[a][b][c][d][e][f]hstack=6";
        string[] BarsArgs(string scale, string format, params string[] tags) => new[] { "-f", "lavfi", "-i", "nullsrc=s=16x16:d=2", "-filter_complex", Bars + "," + scale + "," + format }
            .Concat(new[] { "-frames:v", "30", "-r", "30", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0" }).Concat(tags).ToArray();
        Define("bars_hd709.mkv", _ => BarsArgs("scale=1920x1080:out_color_matrix=bt709:out_range=tv", "format=yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv"));
        Define("bars_sd601.mkv", _ => BarsArgs("scale=720x480:out_color_matrix=bt601:out_range=tv,setsar=32/27", "format=yuv420p", "-colorspace", "smpte170m", "-color_primaries", "smpte170m", "-color_trc", "smpte170m", "-color_range", "tv"));
        Define("bars_sd_untagged.mkv", _ => BarsArgs("scale=720x480:out_color_matrix=bt601:out_range=tv,setsar=32/27,setparams=colorspace=unknown:color_primaries=unknown:color_trc=unknown:range=unknown", "format=yuv420p"));
        Define("bars_hd_untagged.mkv", _ => BarsArgs("scale=1920x1080:out_color_matrix=bt709:out_range=tv,setparams=colorspace=unknown:color_primaries=unknown:color_trc=unknown:range=unknown", "format=yuv420p"));
        Define("bars_hd_full.mkv", _ => BarsArgs("scale=1920x1080:out_color_matrix=bt709:out_range=pc", "format=yuvj420p", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "pc"));
        Define("bars_hdr10.mkv", _ => new[]
        {
            "-f", "lavfi", "-i", "nullsrc=s=16x16:d=2", "-filter_complex",
            Bars + ",scale=1920x1080,format=gbrpf32le,zscale=tin=bt709:pin=bt709:t=smpte2084:p=bt2020:m=bt2020nc:r=tv:npl=203,format=yuv420p10le",
            "-frames:v", "30", "-r", "30", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:lossless=1",
            "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-color_range", "tv"
        });
    }

    public string Root { get; }

    /// <summary>Gets (generating on first use) the fixture file with the given name.</summary>
    public string this[string name] => _files[name].Value;

    /// <summary>A fresh output path in the fixture directory.</summary>
    public string Output(string name) => Path.Combine(Root, Guid.NewGuid().ToString("N")[..8] + "-" + name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
