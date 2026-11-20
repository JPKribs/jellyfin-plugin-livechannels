using System;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The plugin's activity log entries. Channels starting and stopping, and recordings completing, are summed per
/// outcome into one entry, such as "3 channels started", rather than one per event. An administrator stopping a
/// stream from the dashboard is a single entry of its own. Every entry is in the server's display language.
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
        : this(logger, new ActivitySummary(logger, language: () => config is null ? null : BaseTranslations.ServerLanguage(config)), config)
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
            TranslationKeys.ActivityNameChannelStartedOne,
            TranslationKeys.ActivityNameChannelStartedMany,
            (t, language) => Counted(t, language, TranslationKeys.ActivityChannelStartedOne, TranslationKeys.ActivityChannelStartedMany));
        _channelStopped = new(
            "LiveChannels.ChannelStopped",
            TranslationKeys.ActivityNameChannelStoppedOne,
            TranslationKeys.ActivityNameChannelStoppedMany,
            (t, language) => Counted(t, language, TranslationKeys.ActivityChannelStoppedOne, TranslationKeys.ActivityChannelStoppedMany));
        _recordingCompleted = new(
            "LiveChannels.RecordingCompleted",
            TranslationKeys.ActivityNameRecordingCompletedOne,
            TranslationKeys.ActivityNameRecordingCompletedMany,
            (t, language) => Counted(t, language, TranslationKeys.ActivityRecordingCompletedOne, TranslationKeys.ActivityRecordingCompletedMany));
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
    private static string Counted(ActivityTally tally, string language, string one, string many)
    {
        var list = ActivityText.CountedList(tally.Sources);
        return tally.Total == 1
            ? BaseTranslations.Format(one, language, list)
            : BaseTranslations.Format(many, language, tally.Total, list);
    }
}
