using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LiveChannels.Configuration;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests that a configuration save's cache clear wins over a build that was already in flight, and that it clears
/// the directory the running service actually uses even after the configured stream directory changed.
/// </summary>
[Collection(ScheduleCacheCollection.Name)]
public sealed class ScheduleCacheInvalidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lc-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WriteAfterClear_IsDropped()
    {
        var paths = PathsAt(Path.Combine(_root, "a"));
        var service = Create(paths);
        var channel = new Channel { Number = 7, Name = "Seven" };
        var generation = ChannelService.CacheGeneration;

        ChannelService.ClearScheduleCache(paths);

        Assert.False(service.WriteScheduleCache(channel, Loop(), generation));
        Assert.False(File.Exists(Path.Combine(ChannelService.ScheduleDir(paths), "7.json")));

        Assert.True(service.WriteScheduleCache(channel, Loop(), ChannelService.CacheGeneration));
        Assert.True(File.Exists(Path.Combine(ChannelService.ScheduleDir(paths), "7.json")));
    }

    [Fact]
    public void Clear_RemovesTheActiveDirectory_WhenTheConfiguredOneMoved()
    {
        var original = PathsAt(Path.Combine(_root, "old"));
        var service = Create(original);
        var channel = new Channel { Number = 3, Name = "Three" };
        Assert.True(service.WriteScheduleCache(channel, Loop(), ChannelService.CacheGeneration));
        var stale = Path.Combine(ChannelService.ScheduleDir(original), "3.json");
        var strayTemp = Path.Combine(ChannelService.ScheduleDir(original), "3.json.abc.tmp");
        File.WriteAllText(strayTemp, "{");
        var durations = Path.Combine(ChannelService.ScheduleDir(original), "durations.json");
        File.WriteAllText(durations, "{}");

        // The configuration now names a different root; the running service still reads the old one.
        ChannelService.ClearScheduleCache(PathsAt(Path.Combine(_root, "new")));

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(strayTemp));
        Assert.True(File.Exists(durations));
        Assert.True(File.Exists(Path.ChangeExtension(stale, ".superseded")));
    }

    [Fact]
    public void ASave_SetsTheScheduleAside_SoTheRebuildKeepsWhatIsAiring()
    {
        // A save used to delete the cached loop, so the rebuild had nothing to splice onto and the guide (and every
        // live viewer) jumped to an unrelated programme. The loop is now set aside, never served, and the rebuild
        // continues from the programme on air.
        var paths = PathsAt(Path.Combine(_root, "splice"));
        var service = Create(paths);
        var channel = new Channel { Number = 4, Name = "Four" };
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = Entry("A");
        var b = Entry("B");
        var c = Entry("C");
        Assert.True(service.WriteScheduleCache(channel, new ProgramLoop(new List<ProgramEntry> { a, b, c }, epoch), ChannelService.CacheGeneration));

        ChannelService.ClearScheduleCache(paths);
        Assert.False(File.Exists(Path.Combine(ChannelService.ScheduleDir(paths), "4.json")));

        var setAside = service.SupersededLoop(channel);
        Assert.NotNull(setAside);

        // 01:30 into the loop B is on air (started 01:00). The rebuild reorders the loop; B must keep its slot.
        var now = epoch.AddHours(1.5);
        var rebuilt = service.Splice(channel, setAside, new List<ProgramEntry> { c, a, b }, now);
        var airing = service.BuildTimeline(channel, rebuilt, now, now.AddTicks(1))[0];
        Assert.Equal(b.ItemId, airing.Program.ItemId);
        Assert.Equal(epoch.AddHours(1), airing.Start);
    }

    [Fact]
    public void WithoutASave_NothingIsSetAside()
    {
        var paths = PathsAt(Path.Combine(_root, "none"));
        var service = Create(paths);
        Assert.Null(service.SupersededLoop(new Channel { Number = 9, Name = "Nine" }));
    }

    [Fact]
    public void PluginWideSettings_DoNotInvalidateSchedules()
    {
        var stored = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } } };
        var incoming = new PluginConfiguration { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } }, TranscodeVideoBitrateKbps = 8000, SubtitleBold = true };
        Assert.False(Plugin.InvalidatesSchedules(incoming, stored));
    }

    [Fact]
    public void ChannelLanguageAndDirectoryChanges_InvalidateSchedules()
    {
        PluginConfiguration Base() => new() { Channels = { new Channel { Id = "a", Name = "A", Number = 1 } }, DefaultSubtitleLanguage = "eng" };

        var language = Base();
        language.DefaultSubtitleLanguage = "jpn";
        Assert.True(Plugin.InvalidatesSchedules(language, Base()));

        var sameLanguage = Base();
        sameLanguage.DefaultSubtitleLanguage = "ENG ";
        Assert.False(Plugin.InvalidatesSchedules(sameLanguage, Base()));

        var directory = Base();
        directory.StreamDirectory = "/srv/streams";
        Assert.True(Plugin.InvalidatesSchedules(directory, Base()));

        var edited = Base();
        edited.Channels[0].Name = "B";
        Assert.True(Plugin.InvalidatesSchedules(edited, Base()));
    }

    private static ProgramEntry Entry(string title)
        => new(Guid.NewGuid(), title, null, TimeSpan.FromHours(1).Ticks, "/" + title + ".mkv");

    private static ProgramLoop Loop()
        => new(new List<ProgramEntry> { new(Guid.NewGuid(), "m", null, TimeSpan.FromHours(1).Ticks, "/m.mkv") }, null);

    private static IApplicationPaths PathsAt(string cache)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(cache);
        paths.DataPath.Returns(Path.Combine(cache, "data"));
        return paths;
    }

    private static ChannelService Create(IApplicationPaths paths)
        => new(
            Substitute.For<ILibraryManager>(),
            Substitute.For<IMediaSourceManager>(),
            Substitute.For<ISubtitleEncoder>(),
            Substitute.For<IPathManager>(),
            Substitute.For<ILocalizationManager>(),
            Substitute.For<IUserManager>(),
            Substitute.For<IUserDataManager>(),
            paths,
            NullLogger<ChannelService>.Instance);
}

/// <summary>Serialises the tests that touch the schedule cache's process-wide state.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScheduleCacheCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "ScheduleCache";
}
