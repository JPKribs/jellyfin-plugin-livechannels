using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for the concat start position, bounded child processes, atomic logo writes, timers that remember the
/// item they were scheduled for, and the schedule re-anchor rule.
/// </summary>
public sealed class AuditFixTests : IDisposable
{
    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "livechannels-audit-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    private static ProgramEntry Program(string title, TimeSpan duration)
        => new(Guid.NewGuid(), title, null, duration.Ticks, "/media/" + title + ".mkv");

    // ---- Concat start position (computed against the full cycle, missing files skipped lazily) ----

    [Fact]
    public void ConcatPosition_MatchesTheGuide_WhenAnEarlierItemIsMissing()
    {
        // A, B (missing), C, D: one hour each. At 02:30 into the cycle the guide says C is on, 30 minutes in. The
        // old code dropped B before computing the position, so the stream thought D was on: an hour off the guide.
        var programs = new List<ProgramEntry> { Program("A", TimeSpan.FromHours(1)), Program("B", TimeSpan.FromHours(1)), Program("C", TimeSpan.FromHours(1)), Program("D", TimeSpan.FromHours(1)) };
        var now = Epoch.AddHours(2.5);
        var guide = ScheduleCalculator.BuildSchedule(programs, now, now.AddHours(1), Epoch)[0];
        Assert.Equal("C", guide.Program.Title);

        var (airing, offset) = ScheduleCalculator.CurrentProgram(programs, now, Epoch);
        var (index, into, skipped) = StreamSessionService.FirstPlayable(programs, airing, offset, p => !p.EndsWith("/B.mkv", StringComparison.Ordinal));

        Assert.Equal("C", programs[index].Title);
        Assert.Equal(TimeSpan.FromMinutes(30), into);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void ConcatPosition_ChecksOnlyTheAiringFile_OnAHealthyChannel()
    {
        var programs = Enumerable.Range(0, 500).Select(i => Program("P" + i, TimeSpan.FromMinutes(30))).ToList();
        var checks = 0;
        var (index, into, _) = StreamSessionService.FirstPlayable(programs, 7, TimeSpan.FromMinutes(5), _ =>
        {
            checks++;
            return true;
        });

        Assert.Equal(7, index);
        Assert.Equal(TimeSpan.FromMinutes(5), into);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void ConcatPosition_StepsOverAMissingAiringItem_ToTheNextStart()
    {
        var programs = new List<ProgramEntry> { Program("A", TimeSpan.FromHours(1)), Program("B", TimeSpan.FromHours(1)), Program("C", TimeSpan.FromHours(1)) };
        var (index, into, skipped) = StreamSessionService.FirstPlayable(programs, 2, TimeSpan.FromMinutes(10), p => !p.EndsWith("/C.mkv", StringComparison.Ordinal));

        Assert.Equal(0, index);
        Assert.Equal(TimeSpan.Zero, into);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void ConcatPosition_NothingExists_ReportsNone()
    {
        var programs = new List<ProgramEntry> { Program("A", TimeSpan.FromHours(1)), Program("B", TimeSpan.FromHours(1)) };
        Assert.Equal(-1, StreamSessionService.FirstPlayable(programs, 1, TimeSpan.Zero, _ => false).Index);
    }

    [Fact]
    public void ConcatList_LeavesOutMissingFiles_WithinTheHorizon()
    {
        var programs = new List<ProgramEntry> { Program("A", TimeSpan.FromHours(1)), Program("B", TimeSpan.FromHours(1)), Program("C", TimeSpan.FromHours(1)) };
        var list = StreamSessionService.ConcatList(programs, 1, p => !p.EndsWith("/C.mkv", StringComparison.Ordinal), TimeSpan.FromHours(24));
        Assert.Equal(new[] { "B", "A" }, list.Select(p => p.Title));
    }

    [Fact]
    public void ConcatList_ChecksOnlyTheStretchAViewerCanReach()
    {
        // 2000 half-hour items: a tune-in checks a day's worth (48 files), not the whole cycle, and still lists
        // every item so the loop order is untouched.
        var programs = Enumerable.Range(0, 2000).Select(i => Program("P" + i, TimeSpan.FromMinutes(30))).ToList();
        var checks = 0;
        var list = StreamSessionService.ConcatList(programs, 10, _ =>
        {
            checks++;
            return true;
        }, TimeSpan.FromHours(24));

        Assert.Equal(48, checks);
        Assert.Equal(2000, list.Count);
        Assert.Equal("P10", list[0].Title);
    }

    // ---- Bounded child processes ----

    [Fact]
    public async Task RunProcess_KillsAHungProcess_AtTheTimeout()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sleep"))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var (exit, _, error) = await IntroService.RunProcessAsync("/bin/sleep", ["30"], captureOutput: true, TimeSpan.FromMilliseconds(300));
        stopwatch.Stop();

        Assert.Equal(-1, exit);
        Assert.Contains("timed out", error, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "the hung process was waited on instead of killed");
    }

    [Fact]
    public async Task RunProcess_GivesUpOnAProcessThatWillNotGoAway()
    {
        // A detached grandchild holds stderr open after the kill, so the pipe never closes; the same as a process
        // stuck in uninterruptible IO that cannot die. The run must still return shortly after the kill.
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var (exit, _, error) = await IntroService.RunProcessAsync("/bin/sh", ["-c", "(sleep 20 &) ; sleep 20"], captureOutput: false, TimeSpan.FromMilliseconds(300));
        stopwatch.Stop();

        Assert.Equal(-1, exit);
        Assert.Contains("had not exited", error, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < IntroService.ReapTimeout + TimeSpan.FromSeconds(5), "waited on the unkillable process: " + stopwatch.Elapsed);
    }

    [Fact]
    public async Task RunProcess_KeepsOutput_FromANonZeroExit()
    {
        // ffprobe can print every packet and still exit non-zero over a trailing one; the card's end is read from
        // that output regardless of the exit code.
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return;
        }

        var (exit, output, _) = await IntroService.RunProcessAsync("/bin/sh", ["-c", "echo 9.5,0.033; exit 1"], captureOutput: true, TimeSpan.FromSeconds(10));
        Assert.Equal(1, exit);
        Assert.Equal("9.5,0.033", output.Trim());
    }

    [Fact]
    public async Task RunProcess_ReturnsOutputAndExitCode_WhenItFinishes()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/echo"))
        {
            return;
        }

        var (exit, output, _) = await IntroService.RunProcessAsync("/bin/echo", ["hello"], captureOutput: true, TimeSpan.FromSeconds(10));
        Assert.Equal(0, exit);
        Assert.Equal("hello", output.Trim());
    }

    // ---- Atomic logo writes ----

    [Fact]
    public async Task ConcurrentLogoWrites_AllSucceed_AndLeaveACompleteFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "1-logo.png");
        var bytes = Enumerable.Range(0, 512 * 1024).Select(i => (byte)i).ToArray();

        await File.WriteAllBytesAsync(path, bytes);

        // The guide refresh, the start-up warm and a tune-in all resolving the same logo at once, while Jellyfin
        // reads the image for a client. Written in place, a reader caught the file truncated mid-rewrite.
        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if ((await File.ReadAllBytesAsync(path)).Length != bytes.Length)
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref torn);
                }
            }
        });
        for (var round = 0; round < 20; round++)
        {
            var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => LiveChannelsTvService.WriteFileAtomicAsync(path, bytes, CancellationToken.None)));
            await Task.WhenAll(writers);
        }

        await stop.CancelAsync();
        await reader;

        Assert.Equal(0, torn);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task LogoWrite_IntoAnUnwritableFolder_KeepsTheExistingFile_Quietly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "1-logo.png");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Access denied, but the logo is already there: not an error, and nothing is left behind.
            await LiveChannelsTvService.WriteFileAtomicAsync(path, new byte[] { 4, 5, 6 }, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(_dir));
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---- Timers remember their scheduled item ----

    [Fact]
    public void PickSlot_WithoutAStamp_KeepsTheLegacySlotLookup()
    {
        var a = Program("A", TimeSpan.FromHours(1));
        var start = Epoch.AddHours(10);
        var timeline = new List<ScheduledProgram> { new(a, start.AddMinutes(-5), start.AddMinutes(55)) };

        var slot = RecordingService.PickSlot(timeline, new[] { a }, start, null, out var fellBack);
        Assert.Same(a, slot!.Program);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickSlot_AfterAReschedule_RecordsTheScheduledItem_NotTheSlotsNewOccupant()
    {
        // The timer was set on B at 10:00; a reschedule since put A in that slot. The old lookup recorded A.
        var a = Program("A", TimeSpan.FromHours(1));
        var b = Program("B", TimeSpan.FromMinutes(45));
        var start = Epoch.AddHours(10);
        var timeline = new List<ScheduledProgram> { new(a, start, start.AddHours(1)) };

        var slot = RecordingService.PickSlot(timeline, new[] { a, b }, start, b.ItemId, out var fellBack);
        Assert.Same(b, slot!.Program);
        Assert.Equal(start, slot.Start);
        Assert.Equal(start.AddMinutes(45), slot.Stop);
        Assert.False(fellBack);
    }

    [Fact]
    public void PickSlot_ScheduledItemGoneFromTheChannel_FallsBackAndSaysSo()
    {
        var a = Program("A", TimeSpan.FromHours(1));
        var start = Epoch.AddHours(10);
        var timeline = new List<ScheduledProgram> { new(a, start, start.AddHours(1)) };

        var slot = RecordingService.PickSlot(timeline, new[] { a }, start, Guid.NewGuid(), out var fellBack);
        Assert.Same(a, slot!.Program);
        Assert.True(fellBack);
    }

    [Fact]
    public void ScheduledItem_SurvivesAReload_AndAClientEdit()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dir);
        var item = Guid.NewGuid();
        var timer = new TimerInfo { ChannelId = "chan", ProgramId = "chan_1", Name = "Show", StartDate = DateTime.UtcNow.AddHours(1), EndDate = DateTime.UtcNow.AddHours(2) };
        RecordingService.SetScheduledItem(timer, item);
        var id = new TimerService(paths, NullLogger<TimerService>.Instance).SaveTimer(timer);

        // A restart reads it back from disk.
        var reloaded = new TimerService(paths, NullLogger<TimerService>.Instance);
        Assert.Equal(item, RecordingService.ScheduledItem(Assert.Single(reloaded.GetTimers())));

        // A client's edit is rebuilt from its DTO, which carries no provider ids; the stamp must survive it.
        reloaded.SaveTimer(new TimerInfo { Id = id, ChannelId = "chan", ProgramId = "chan_1", Name = "Show (edited)", StartDate = timer.StartDate, EndDate = timer.EndDate, PostPaddingSeconds = 60 });
        var edited = Assert.Single(new TimerService(paths, NullLogger<TimerService>.Instance).GetTimers());
        Assert.Equal("Show (edited)", edited.Name);
        Assert.Equal(item, RecordingService.ScheduledItem(edited));
    }

    [Fact]
    public void LegacyTimer_WithoutAStamp_StillLoads()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "livechannels"));
        File.WriteAllText(
            Path.Combine(_dir, "livechannels", "timers.json"),
            "{\"Timers\":[{\"Id\":\"lc_timer_old\",\"ChannelId\":\"chan\",\"ProgramId\":\"chan_1\",\"Name\":\"Old\",\"StartDate\":\"2099-01-01T00:00:00Z\",\"EndDate\":\"2099-01-01T01:00:00Z\"}],\"SeriesTimers\":[]}");
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dir);

        var timer = Assert.Single(new TimerService(paths, NullLogger<TimerService>.Instance).GetTimers());
        Assert.Equal("lc_timer_old", timer.Id);
        Assert.Null(RecordingService.ScheduledItem(timer));
    }

    // ---- Re-anchor only on schedule-affecting saves ----

    [Fact]
    public void AffectsSchedule_IgnoresPluginWideSettings()
    {
        var stored = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } } };
        var incoming = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } }, TranscodeVideoBitrateKbps = 8000, SubtitleBold = true };

        Assert.False(Plugin.AffectsSchedule(incoming, stored));
    }

    [Fact]
    public void AffectsSchedule_SeesChannelAndPopularEdits()
    {
        var stored = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } } };

        var renamed = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "B", Number = 1 } } };
        Assert.True(Plugin.AffectsSchedule(renamed, stored));

        var added = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 }, new Channel { Id = "b", Name = "B", Number = 2 } } };
        Assert.True(Plugin.AffectsSchedule(added, stored));

        var popular = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } } };
        popular.PopularChannel.Name = "Trending";
        Assert.True(Plugin.AffectsSchedule(popular, stored));
    }
}
