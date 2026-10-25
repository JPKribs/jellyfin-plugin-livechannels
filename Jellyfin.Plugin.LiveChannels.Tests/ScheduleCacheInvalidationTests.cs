using System;
using System.Collections.Generic;
using System.IO;
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
    }

    private static ProgramLoop Loop()
        => new(new List<ProgramEntry> { new(Guid.NewGuid(), "m", null, TimeSpan.FromHours(1).Ticks, "/m.mkv") }, null);

    private static IApplicationPaths PathsAt(string cache)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.CachePath.Returns(cache);
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
