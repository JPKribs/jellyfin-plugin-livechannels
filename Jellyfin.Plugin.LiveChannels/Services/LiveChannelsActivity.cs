using System;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The plugin's activity log entries. Channels starting and stopping, and recordings completing, are summed per
/// outcome into one entry (such as "3 Channels Started") rather than one per event; an administrator stopping a
/// stream from the dashboard is a single entry of its own. Each overview is in the server's display language.
/// </summary>
public sealed class LiveChannelsActivity : IDisposable
{
    private readonly ActivitySummary _summary;
    private readonly IServerConfigurationManager? _config;
    private readonly ActivityOutcome _channelStarted;
    private readonly ActivityOutcome _channelStopped;
    private readonly ActivityOutcome _recordingCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsActivity"/> class.
    /// </summary>
    /// <param name="logger">The activity log writer.</param>
    /// <param name="config">The server configuration, whose display language the entries use, or <c>null</c> for US English.</param>
    public LiveChannelsActivity(ActivityLogger logger, IServerConfigurationManager? config = null)
        : this(logger, new ActivitySummary(logger), config)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsActivity"/> class with a given summary, for tests.
    /// </summary>
    /// <param name="logger">The activity log writer.</param>
    /// <param name="summary">The summary that sums batched events.</param>
    /// <param name="config">The server configuration, whose display language the entries use, or <c>null</c> for US English.</param>
    public LiveChannelsActivity(ActivityLogger logger, ActivitySummary summary, IServerConfigurationManager? config = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(summary);
        Logger = logger;
        _summary = summary;
        _config = config;
        _channelStarted = new(
            "LiveChannels.ChannelStarted",
            "Started",
            t => Counted(t, TranslationKeys.ActivityChannelStartedOne, TranslationKeys.ActivityChannelStartedMany),
            Noun: "Channel");
        _channelStopped = new(
            "LiveChannels.ChannelStopped",
            "Stopped",
            t => Counted(t, TranslationKeys.ActivityChannelStoppedOne, TranslationKeys.ActivityChannelStoppedMany),
            Noun: "Channel");
        _recordingCompleted = new(
            "LiveChannels.RecordingCompleted",
            "Completed",
            t => Counted(t, TranslationKeys.ActivityRecordingCompletedOne, TranslationKeys.ActivityRecordingCompletedMany),
            Noun: "Recording");
    }

    /// <summary>Gets the activity log writer, for single entries.</summary>
    public ActivityLogger Logger { get; }

    /// <summary>Counts a channel whose encoder started.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void ChannelStart(string channelName) => _summary.Add(_channelStarted, channelName);

    /// <summary>Counts a channel whose encoder stopped because nobody was watching.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void ChannelIdleStop(string channelName) => _summary.Add(_channelStopped, channelName);

    /// <summary>Counts a completed recording.</summary>
    /// <param name="recordingName">The recording's name.</param>
    public void RecordingComplete(string recordingName) => _summary.Add(_recordingCompleted, recordingName);

    /// <summary>Writes the single entry for a stream an administrator stopped from the dashboard.</summary>
    /// <param name="channelName">The channel's name.</param>
    public void StreamKilled(string channelName) => Logger.Log(
        BaseTranslations.Format(TranslationKeys.ActivityStreamKilledName, Language, channelName),
        "LiveChannels.ChannelStopped",
        ActivityText.Sentence(BaseTranslations.Format(TranslationKeys.ActivityStreamKilled, Language, channelName)));

    /// <summary>Writes whatever is waiting, now.</summary>
    public void Flush() => _summary.Flush();

    /// <inheritdoc />
    public void Dispose() => _summary.Dispose();

    // The server's display language, or US English with no server configuration.
    private string? Language => _config is null ? null : BaseTranslations.ServerLanguage(_config);

    // The overview of a summed entry, with one string for a single event and one for several.
    private string Counted(ActivityTally tally, string one, string many)
    {
        var list = ActivityText.CountedList(tally.Sources);
        return tally.Total == 1
            ? BaseTranslations.Format(one, Language, list)
            : BaseTranslations.Format(many, Language, tally.Total, list);
    }
}
