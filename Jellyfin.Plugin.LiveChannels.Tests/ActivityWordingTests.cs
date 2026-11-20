using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LiveChannels.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The wording of the plugin's activity log entries: channel starts, idle stops, and completed recordings are
/// summed per outcome, and a stream stopped from the dashboard is a single entry.
/// </summary>
public class ActivityWordingTests
{
    private readonly List<ActivityLog> _written = new();

    private LiveChannelsActivity Create()
    {
        var manager = Substitute.For<IActivityManager>();
        manager.When(m => m.CreateAsync(Arg.Any<ActivityLog>())).Do(c =>
        {
            lock (_written)
            {
                _written.Add(c.Arg<ActivityLog>());
            }
        });
        return new LiveChannelsActivity(new ActivityLogger(manager, NullLogger<ActivityLogger>.Instance));
    }

    [Fact]
    public void ChannelStarts_AreSummedByChannel()
    {
        using var activity = Create();
        activity.ChannelStart("News");
        activity.ChannelStart("Sports");
        activity.ChannelStart("News");
        activity.Flush();

        var entry = Assert.Single(_written);
        Assert.Equal("3 channels started", entry.Name);
        Assert.Equal("3 channels started encoding: News (2), Sports (1)", entry.ShortOverview);
        Assert.Equal("LiveChannels.ChannelStarted", entry.Type);
        Assert.Equal(LogLevel.Information, entry.LogSeverity);
    }

    [Fact]
    public void IdleStops_AreSummedByChannel()
    {
        using var activity = Create();
        activity.ChannelIdleStop("News");
        activity.Flush();

        var entry = Assert.Single(_written);
        Assert.Equal("1 channel stopped", entry.Name);
        Assert.Equal("1 channel stopped encoding with no viewers left: News (1)", entry.ShortOverview);
        Assert.Equal("LiveChannels.ChannelStopped", entry.Type);
    }

    [Fact]
    public void Recordings_AreSummedByName()
    {
        using var activity = Create();
        activity.RecordingComplete("Evening News");
        activity.RecordingComplete("Evening News");
        activity.Flush();

        var entry = Assert.Single(_written);
        Assert.Equal("2 recordings completed", entry.Name);
        Assert.Equal("2 recordings were completed: Evening News (2)", entry.ShortOverview);
        Assert.Equal("LiveChannels.RecordingCompleted", entry.Type);
    }

    [Fact]
    public void Outcomes_WriteOneEntryEach()
    {
        using var activity = Create();
        activity.ChannelStart("News");
        activity.ChannelIdleStop("News");
        activity.RecordingComplete("Movie");
        activity.Flush();

        Assert.Equal(
            new[] { "1 channel started", "1 channel stopped", "1 recording completed" },
            _written.Select(e => e.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void DashboardStop_IsASingleEntry()
    {
        using var activity = Create();
        activity.StreamKilled("News");

        var entry = Assert.Single(_written);
        Assert.Equal("Channel stopped for News", entry.Name);
        Assert.Equal("The stream for News was stopped from the dashboard", entry.ShortOverview);
        Assert.Equal("LiveChannels.ChannelStopped", entry.Type);
    }

    [Fact]
    public void Dispose_FlushesWaitingEvents()
    {
        var activity = Create();
        activity.ChannelStart("News");
        activity.Dispose();

        Assert.Equal("1 channel started", Assert.Single(_written).Name);
    }
}
