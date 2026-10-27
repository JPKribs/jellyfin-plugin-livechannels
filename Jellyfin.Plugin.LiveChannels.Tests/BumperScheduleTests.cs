using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests that the Up Next bumper is part of each item's slot and never pushes an item later: the slot is the bumper
/// lead plus the item on the guide, the stream plays the head of a slot before its item, switching bumpers on or
/// off keeps the item on air exactly where it is, and the lead survives the schedule cache.
/// </summary>
[Collection(ScheduleCacheCollection.Name)]
public sealed class BumperScheduleTests : IDisposable
{
    private static readonly TimeSpan Lead = BumperService.BumperLength;
    private static readonly DateTime Noon = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lc-bumper-schedule-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheBumperIsPartOfTheSlot_SoAnHourMovieAtNoonStartsAt12_00_10AndEndsAt13_00_10()
    {
        var movie = Item("Movie", TimeSpan.FromHours(1));
        var next = Item("Next", TimeSpan.FromMinutes(30));
        var loop = new ProgramLoop(new[] { movie, next }, Noon, Lead.Ticks);

        var schedule = ScheduleCalculator.BuildSchedule(loop, Noon.AddMinutes(1), Noon.AddHours(2), Noon);

        Assert.Equal(Noon, schedule[0].Start);
        Assert.Equal(Noon + Lead, schedule[0].ContentStart);
        Assert.Equal(Noon.AddHours(1) + Lead, schedule[0].Stop);
        Assert.Equal(schedule[0].Stop, schedule[1].Start);
        Assert.Equal(schedule[1].Start + Lead, schedule[1].ContentStart);
        Assert.Equal(TimeSpan.FromMinutes(30) + Lead, schedule[1].Stop - schedule[1].Start);
    }

    [Fact]
    public void WithoutALead_SlotsAreTheItemsThemselves()
    {
        var loop = new ProgramLoop(new[] { Item("A", TimeSpan.FromHours(1)) }, Noon);
        var slot = ScheduleCalculator.BuildSchedule(loop, Noon, Noon.AddHours(1), Noon)[0];
        Assert.Equal(TimeSpan.Zero, slot.Lead);
        Assert.Equal(slot.Start, slot.ContentStart);
        Assert.Equal(Noon.AddHours(1), slot.Stop);
    }

    [Fact]
    public void CurrentProgram_ReportsTheOffsetIntoTheSlot_BumperFirst()
    {
        var loop = new ProgramLoop(new[] { Item("A", TimeSpan.FromMinutes(10)), Item("B", TimeSpan.FromMinutes(10)) }, Noon, Lead.Ticks);

        Assert.Equal((0, TimeSpan.FromSeconds(7)), ScheduleCalculator.CurrentProgram(loop, Noon.AddSeconds(7), Noon));
        Assert.Equal((0, TimeSpan.FromMinutes(5)), ScheduleCalculator.CurrentProgram(loop, Noon.AddMinutes(5), Noon));

        // B's slot starts after A's whole slot: ten minutes plus the bumper in.
        Assert.Equal((1, TimeSpan.Zero), ScheduleCalculator.CurrentProgram(loop, Noon.AddMinutes(10) + Lead, Noon));
        Assert.Equal((1, TimeSpan.FromSeconds(14)), ScheduleCalculator.CurrentProgram(loop, Noon.AddMinutes(10) + Lead + TimeSpan.FromSeconds(14), Noon));

        // The whole cycle is both items plus both bumpers.
        Assert.Equal((0, TimeSpan.Zero), ScheduleCalculator.CurrentProgram(loop, Noon.AddMinutes(20) + Lead + Lead, Noon));
    }

    [Fact]
    public void TheDaypartChainCountsTheLeadToo()
    {
        var a = Item("A", TimeSpan.FromMinutes(30));
        var loop = new ProgramLoop(new[] { a, Item("B", TimeSpan.FromMinutes(30)) }, Noon, Lead.Ticks);
        var blocks = new[] { new ResolvedRatingBlock(new RatingWindow(null, null, true), false, 0, 12 * 60) };

        var schedule = DaypartSchedule.BuildResumable(loop, blocks, 0, TimeZoneInfo.Utc, Noon, Noon, Noon.AddHours(2), "chan", null, out _, exactStart: true);

        Assert.All(schedule, s => Assert.Equal(Lead, s.Lead));
        Assert.All(schedule, s => Assert.Equal(TimeSpan.FromMinutes(30) + Lead, s.Stop - s.Start));
        for (var i = 1; i < schedule.Count; i++)
        {
            Assert.Equal(schedule[i - 1].Stop, schedule[i].Start);
        }
    }

    [Fact]
    public void SwitchingBumpersOn_MidItem_KeepsTheItemOnAirWhereItIs()
    {
        var service = Create();
        var channel = new Channel { Id = "c", Number = 41 };
        var items = new[] { Item("A", TimeSpan.FromHours(1)), Item("B", TimeSpan.FromHours(1)) };
        var before = new ProgramLoop(items, Noon);
        var now = Noon.AddMinutes(30);

        var after = service.Splice(channel, before, items, now, Lead.Ticks);

        Assert.Equal(Lead.Ticks, after.LeadTicks);
        var slot = ScheduleCalculator.BuildSchedule(after, now, now.AddTicks(1), ScheduleCalculator.AnchorFor(after))[0];
        Assert.Same(items[0], slot.Program);
        Assert.Equal(Noon, slot.ContentStart);
        Assert.Equal(Noon - Lead, slot.Start);

        // The next item now gets its own bumper right after A ends.
        var next = ScheduleCalculator.BuildSchedule(after, Noon.AddHours(1), Noon.AddHours(1).AddTicks(1), ScheduleCalculator.AnchorFor(after))[0];
        Assert.Same(items[1], next.Program);
        Assert.Equal(Noon.AddHours(1), next.Start);
        Assert.Equal(Noon.AddHours(1) + Lead, next.ContentStart);
    }

    [Fact]
    public void SwitchingBumpersOff_MidItem_KeepsTheItemOnAirWhereItIs()
    {
        var service = Create();
        var channel = new Channel { Id = "c", Number = 42 };
        var items = new[] { Item("A", TimeSpan.FromHours(1)), Item("B", TimeSpan.FromHours(1)) };
        var before = new ProgramLoop(items, Noon, Lead.Ticks);
        var now = Noon.AddMinutes(30);

        var after = service.Splice(channel, before, items, now, 0);

        var slot = ScheduleCalculator.BuildSchedule(after, now, now.AddTicks(1), ScheduleCalculator.AnchorFor(after))[0];
        Assert.Same(items[0], slot.Program);
        Assert.Equal(Noon + Lead, slot.ContentStart);
        Assert.Equal(TimeSpan.Zero, slot.Lead);
    }

    [Fact]
    public void SwitchingBumpersOff_DuringTheBumper_StartsTheItemAtItsSlot_NeverLater()
    {
        var service = Create();
        var channel = new Channel { Id = "c", Number = 43 };
        var items = new[] { Item("A", TimeSpan.FromHours(1)) };
        var before = new ProgramLoop(items, Noon, Lead.Ticks);
        var now = Noon.AddSeconds(5);

        var after = service.Splice(channel, before, items, now, 0);

        var slot = ScheduleCalculator.BuildSchedule(after, now, now.AddTicks(1), ScheduleCalculator.AnchorFor(after))[0];
        Assert.Equal(Noon, slot.ContentStart);
        Assert.True(slot.ContentStart <= Noon + Lead);
    }

    [Fact]
    public void TheLeadSurvivesTheScheduleCache()
    {
        var paths = Paths();
        var service = Create(paths);
        var channel = new Channel { Id = "c", Number = 44, ShowBumpers = true };
        var loop = new ProgramLoop(new[] { Item("A", TimeSpan.FromHours(1)) }, Noon, Lead.Ticks);

        Assert.True(service.WriteScheduleCache(channel, loop, ChannelService.CacheGeneration));
        var read = Create(paths).TryReadScheduleCache(channel);

        Assert.NotNull(read);
        Assert.Equal(Lead.Ticks, read!.LeadTicks);
        Assert.Equal(Noon, read.AnchorUtc);
    }

    [Fact]
    public void AScheduleCachedWithoutBumpers_GainsTheLeadOnRead_WithoutMovingTheItemOnAir()
    {
        var paths = Paths();
        var service = Create(paths);
        var channel = new Channel { Id = "c", Number = 45, ShowBumpers = true };
        var items = new[] { Item("A", TimeSpan.FromDays(400)) };

        // Anchored far enough back that the item is on air now.
        var anchor = DateTime.UtcNow.AddHours(-1);
        Assert.True(service.WriteScheduleCache(channel, new ProgramLoop(items, anchor), ChannelService.CacheGeneration));
        service.ReleaseFromMemory(channel.Number);

        var programs = service.ResolvePrograms(channel, keepInMemory: false);

        Assert.Equal(Lead.Ticks, ProgramLoop.LeadOf(programs));
        var now = DateTime.UtcNow;
        var slot = ScheduleCalculator.BuildSchedule(programs, now, now.AddTicks(1), ScheduleCalculator.AnchorFor(programs))[0];
        Assert.Equal(anchor, slot.ContentStart);

        // And it was written back, so the next read needs no re-splice.
        Assert.Equal(Lead.Ticks, Create(paths).TryReadScheduleCache(channel)!.LeadTicks);
    }

    [Fact]
    public void LeadFor_FollowsTheChannelSetting()
    {
        Assert.Equal(Lead.Ticks, ChannelService.LeadFor(new Channel { ShowBumpers = true }));
        Assert.Equal(0, ChannelService.LeadFor(new Channel { ShowBumpers = false }));
        Assert.True(new Channel().ShowBumpers);
    }

    [Fact]
    public void TogglingBumpers_InvalidatesTheSchedule()
    {
        var stored = new Configuration.PluginConfiguration { Channels = { new Channel { Id = "a", Number = 1 } } };
        var incoming = new Configuration.PluginConfiguration { Channels = { new Channel { Id = "a", Number = 1, ShowBumpers = false } } };
        Assert.True(Plugin.InvalidatesSchedules(incoming, stored));

        var popular = new Configuration.PluginConfiguration();
        popular.PopularChannel.ShowBumpers = false;
        Assert.True(Plugin.InvalidatesSchedules(popular, new Configuration.PluginConfiguration()));
    }

    [Fact]
    public void SchedulesFromBeforeTheMove_AreMovedIntoTheDataFolder()
    {
        var paths = Paths();
        var legacy = Path.Combine(ChannelService.ResolveStreamRoot(paths), ChannelService.ScheduleDirName);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "5.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "durations.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "5.json.x.tmp"), "{");

        Create(paths);

        var current = ChannelService.ScheduleDir(paths);
        Assert.StartsWith(Path.Combine(paths.DataPath, "livechannels"), current, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(current, "5.json")));
        Assert.True(File.Exists(Path.Combine(current, "durations.json")));
        Assert.False(File.Exists(Path.Combine(current, "5.json.x.tmp")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void ContentStep_PlaysTheItemFromItsStartAfterTheBumper_OrFromTheClockPastIt()
    {
        var item = Item("A", TimeSpan.FromMinutes(10));

        // Joined during the bumper: the item starts from zero.
        var during = StreamSessionService.ContentStep(new StreamStep(item, TimeSpan.FromSeconds(6), null, Lead));
        Assert.Equal(TimeSpan.Zero, during.Offset);
        Assert.Equal(TimeSpan.Zero, during.Lead);

        // Joined past it: the item starts where the clock is.
        var after = StreamSessionService.ContentStep(new StreamStep(item, TimeSpan.FromMinutes(2), null, Lead));
        Assert.Equal(TimeSpan.FromMinutes(2) - Lead, after.Offset);

        // A playback cap counts from the join point, so the bumper part comes off it.
        var capped = StreamSessionService.ContentStep(new StreamStep(item, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), Lead));
        Assert.Equal(TimeSpan.FromMinutes(5) - (Lead - TimeSpan.FromSeconds(5)), capped.DurationLimit);

        // No bumper: unchanged.
        var plain = new StreamStep(item, TimeSpan.FromSeconds(30), null);
        Assert.Equal(plain, StreamSessionService.ContentStep(plain));
    }

    [Fact]
    public void DuringBumper_IsTheSlotHeadBeforeTheItemStarts()
    {
        var item = Item("A", TimeSpan.FromMinutes(10));
        var timeline = new[] { new ScheduledProgram(item, Noon, Noon.AddMinutes(10) + Lead) { Lead = Lead } };

        Assert.True(StreamSessionService.DuringBumper(timeline, Noon));
        Assert.True(StreamSessionService.DuringBumper(timeline, Noon.AddSeconds(7)));
        Assert.False(StreamSessionService.DuringBumper(timeline, Noon + Lead));
        Assert.False(StreamSessionService.DuringBumper(timeline, Noon.AddMinutes(5)));
        Assert.False(StreamSessionService.DuringBumper(new[] { new ScheduledProgram(item, Noon, Noon.AddMinutes(10)) }, Noon));
    }

    [Fact]
    public void TheLoopSchedule_StepsSlotBySlot_CarryingTheLeadAndTheNextItem()
    {
        var a = Item("A", TimeSpan.FromMinutes(10));
        var b = Item("B", TimeSpan.FromMinutes(20));
        var loop = new ProgramLoop(new[] { a, b }, Noon, Lead.Ticks);
        var schedule = new LoopStreamSchedule(loop, 0, TimeSpan.FromSeconds(3), Noon.AddSeconds(3));

        Assert.Equal(Lead, schedule.Current.Lead);
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.Current.Offset);

        // The next slots, and when each starts: A's whole slot (bumper included), then B's.
        Assert.Equal((b, Noon.AddMinutes(10) + Lead), schedule.Following(1));
        Assert.Equal((a, Noon.AddMinutes(30) + Lead + Lead), schedule.Following(2));

        schedule.Advance();
        Assert.Same(b, schedule.Current.Program);
        Assert.Equal(TimeSpan.Zero, schedule.Current.Offset);
        Assert.Equal((a, Noon.AddMinutes(30) + Lead + Lead), schedule.Following(1));
    }

    [Fact]
    public void ASlotWithinTheBoundaryTolerance_SnapsOnTheSlotsWholeLength()
    {
        var a = Item("A", TimeSpan.FromMinutes(10));
        var loop = new ProgramLoop(new[] { a, Item("B", TimeSpan.FromMinutes(10)) }, Noon, Lead.Ticks);

        // A second before the slot (bumper included) ends is the next slot's start; a second before the item's
        // own length is still well inside the slot.
        Assert.Equal((1, TimeSpan.Zero), StreamScheduleMath.Snap(loop, 0, TimeSpan.FromMinutes(10) + Lead - TimeSpan.FromSeconds(1)));
        Assert.Equal((0, TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1)), StreamScheduleMath.Snap(loop, 0, TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ALoopWithBumpers_NeverStreamsAsOneContinuousPlaylist()
    {
        var item = new ProgramEntry(Guid.NewGuid(), "A", null, TimeSpan.FromMinutes(10).Ticks, "/a.mkv") { SourceHeight = 1080, SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2 };
        Assert.True(StreamSessionService.ConcatSuits(new ProgramLoop(new[] { item }, Noon)));
        Assert.False(StreamSessionService.ConcatSuits(new ProgramLoop(new[] { item }, Noon, Lead.Ticks)));
    }

    private static ProgramEntry Item(string title, TimeSpan length)
        => new(Guid.NewGuid(), title, null, length.Ticks, "/media/" + title + ".mkv");

    private IApplicationPaths Paths()
    {
        var paths = Substitute.For<IApplicationPaths>();
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        paths.CachePath.Returns(Path.Combine(root, "cache"));
        paths.DataPath.Returns(Path.Combine(root, "data"));
        return paths;
    }

    private ChannelService Create(IApplicationPaths? paths = null)
        => new(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths ?? Paths(),
            NullLogger<ChannelService>.Instance);
}
