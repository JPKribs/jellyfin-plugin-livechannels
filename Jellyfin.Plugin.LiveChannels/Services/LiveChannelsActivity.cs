using System;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The plugin's activity log entries. Channels starting and stopping, and recordings completing, are summed per
/// outcome into one entry (such as "3 Channels Started") rather than one per event; an administrator stopping a
/// stream from the dashboard is a single entry of its own.
/// </summary>
public sealed class LiveChannelsActivity : IDisposable
{
    /// <summary>Channels whose encoder started, counted by channel name.</summary>
    public static readonly ActivityOutcome ChannelStarted = new(
        "LiveChannels.ChannelStarted",
        "Started",
        t => ActivityText.Count(t.Total, "channel") + " started encoding: " + ActivityText.CountedList(t.Sources),
        Noun: "Channel");

    /// <summary>Channels whose encoder stopped after their last viewer left, counted by channel name.</summary>
    public static readonly ActivityOutcome ChannelStopped = new(
        "LiveChannels.ChannelStopped",
        "Stopped",
        t => ActivityText.Count(t.Total, "channel") + " stopped encoding with no viewers left: " + ActivityText.CountedList(t.Sources),
        Noun: "Channel");

    /// <summary>Recordings saved to the recordings folder, counted by recording name.</summary>
    public static readonly ActivityOutcome RecordingCompleted = new(
        "LiveChannels.RecordingCompleted",
        "Completed",
        t => ActivityText.Count(t.Total, "recording") + " " + ActivityText.WasOrWere(t.Total) + " completed: " + ActivityText.CountedList(t.Sources),
        Noun: "Recording");

    private readonly ActivitySummary _summary;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsActivity"/> class.
    /// </summary>
    /// <param name="logger">The activity log writer.</param>
    public LiveChannelsActivity(ActivityLogger logger)
        : this(logger, new ActivitySummary(logger))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsActivity"/> class with a given summary, for tests.
    /// </summary>
    /// <param name="logger">The activity log writer.</param>
    /// <param name="summary">The summary that sums batched events.</param>
    public LiveChannelsActivity(ActivityLogger logger, ActivitySummary summary)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(summary);
        Logger = logger;
        _summary = summary;
    }

    /// <summary>Gets the activity log writer, for single entries.</summary>
    public ActivityLogger Logger { get; }

    /// <summary>Counts a channel whose encoder started.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void ChannelStart(string channelName) => _summary.Add(ChannelStarted, channelName);

    /// <summary>Counts a channel whose encoder stopped because nobody was watching.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void ChannelIdleStop(string channelName) => _summary.Add(ChannelStopped, channelName);

    /// <summary>Counts a completed recording.</summary>
    /// <param name="recordingName">The recording's name.</param>
    public void RecordingComplete(string recordingName) => _summary.Add(RecordingCompleted, recordingName);

    /// <summary>Writes the single entry for a stream an administrator stopped from the dashboard.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void StreamKilled(string channelName) => Logger.Log(
        "Channel Stopped for " + channelName,
        "LiveChannels.ChannelStopped",
        ActivityText.Sentence("the stream for " + channelName + " was stopped from the dashboard"));

    /// <summary>Writes whatever is waiting, now.</summary>
    public void Flush() => _summary.Flush();

    /// <inheritdoc />
    public void Dispose() => _summary.Dispose();
}
