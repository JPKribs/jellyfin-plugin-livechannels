using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// The plugin's activity log entries. Channels starting and stopping, and recordings completing, are summed per
/// outcome into one entry, such as "Channels started", rather than one per event. An administrator stopping a
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
            (t, language) => Listed(t, language, TranslationKeys.ActivityChannelStarted));
        _channelStopped = new(
            "LiveChannels.ChannelStopped",
            TranslationKeys.ActivityNameChannelStoppedOne,
            TranslationKeys.ActivityNameChannelStoppedMany,
            (t, language) => Listed(t, language, TranslationKeys.ActivityChannelStopped));
        _recordingCompleted = new(
            "LiveChannels.RecordingCompleted",
            TranslationKeys.ActivityNameRecordingCompletedOne,
            TranslationKeys.ActivityNameRecordingCompletedMany,
            (t, language) => Listed(t, language, TranslationKeys.ActivityRecordingCompleted));
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
    /// <param name="userName">The name of the administrator who stopped it, or <c>null</c> when the request named no user.</param>
    /// <param name="userId">The administrator's id, or empty when the request named no user.</param>
    public void StreamKilled(string channelName, string? userName = null, Guid userId = default)
    {
        // The overview starts with the user's name as it is written, so it is not passed through ActivityText.Sentence.
        var overview = string.IsNullOrEmpty(userName)
            ? BaseTranslations.Format(TranslationKeys.ActivityStreamKilled, Language, channelName)
            : BaseTranslations.Format(TranslationKeys.ActivityStreamKilledBy, Language, userName, channelName);
        Logger.Log(
            BaseTranslations.Get(TranslationKeys.ActivityNameChannelStoppedOne, Language),
            "LiveChannels.ChannelStopped",
            overview,
            userId: userId);
    }

    /// <summary>Writes whatever is waiting, now.</summary>
    public void Flush() => _summary.Flush();

    /// <inheritdoc />
    public void Dispose() => _summary.Dispose();

    // The server's display language, or US English with no server configuration.
    private string? Language => _config is null ? null : BaseTranslations.ServerLanguage(_config);

    // The overview of a summed entry, which names each channel or recording once.
    private static string Listed(ActivityTally tally, string language, string key)
        => BaseTranslations.Format(key, language, NamedList(tally.Sources));

    /// <summary>
    /// Lists names, the most frequent first, with a count in brackets only for a name seen more than once,
    /// such as "News (2), Sports".
    /// </summary>
    /// <param name="counts">The counts by name. A count of zero or less is left out, and equal counts sort by name.</param>
    /// <returns>The list, or an empty string when there is nothing to list.</returns>
    private static string NamedList(IReadOnlyDictionary<string, int> counts) => string.Join(", ", counts
        .Where(p => p.Value > 0)
        .OrderByDescending(p => p.Value)
        .ThenBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => p.Value == 1 ? p.Key : string.Create(CultureInfo.InvariantCulture, $"{p.Key} ({p.Value})")));
}
