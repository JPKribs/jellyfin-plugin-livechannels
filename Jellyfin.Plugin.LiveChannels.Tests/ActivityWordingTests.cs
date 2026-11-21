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
        Assert.Equal("Channels started", entry.Name);
        Assert.Equal("Started encoding News (2), Sports", entry.ShortOverview);
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
        Assert.Equal("Channel stopped", entry.Name);
        Assert.Equal("Stopped encoding News with no viewers left", entry.ShortOverview);
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
        Assert.Equal("Recordings completed", entry.Name);
        Assert.Equal("Recorded Evening News (2)", entry.ShortOverview);
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
            new[] { "Channel started", "Channel stopped", "Recording completed" },
            _written.Select(e => e.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void DashboardStop_IsASingleEntry()
    {
        using var activity = Create();
        activity.StreamKilled("News");

        var entry = Assert.Single(_written);
        Assert.Equal("Channel stopped", entry.Name);
        Assert.Equal("The stream for News was stopped from the dashboard", entry.ShortOverview);
        Assert.Equal("LiveChannels.ChannelStopped", entry.Type);
    }

    [Fact]
    public void DashboardStop_NamesTheAdministrator()
    {
        using var activity = Create();
        var userId = System.Guid.NewGuid();
        activity.StreamKilled("News", "johnny", userId);

        var entry = Assert.Single(_written);
        Assert.Equal("Channel stopped", entry.Name);
        Assert.Equal("johnny stopped the stream for News from the dashboard", entry.ShortOverview);
        Assert.Equal(userId, entry.UserId);
    }

    [Fact]
    public void RecordingCompleted_NamesASingleRecordingWithNoCount()
    {
        using var activity = Create();
        activity.RecordingComplete("Evening News");
        activity.Flush();

        var entry = Assert.Single(_written);
        Assert.Equal("Recording completed", entry.Name);
        Assert.Equal("Recorded Evening News", entry.ShortOverview);
    }

    [Fact]
    public void Dispose_FlushesWaitingEvents()
    {
        var activity = Create();
        activity.ChannelStart("News");
        activity.Dispose();

        Assert.Equal("Channel started", Assert.Single(_written).Name);
    }
}
