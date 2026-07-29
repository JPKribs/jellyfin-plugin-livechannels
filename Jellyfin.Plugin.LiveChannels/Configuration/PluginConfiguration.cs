using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveChannels.Configuration;

/// <summary>
/// Single configuration object for the plugin. XML-serialized by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The start-up buffer used when none is configured.</summary>
    public const int DefaultStartupBufferSeconds = 12;

    /// <summary>The smallest start-up buffer accepted. Below this the player joins on the encoder's heels.</summary>
    public const int MinStartupBufferSeconds = 4;

    /// <summary>The largest start-up buffer accepted, bounded by the rolling segment window on disk.</summary>
    public const int MaxStartupBufferSeconds = 60;

    /// <summary>Gets or sets the configured virtual channels.</summary>
    public List<Channel> Channels { get; set; } = new();

    /// <summary>Gets or sets the UTC moment the configuration was last saved. Channels with time-of-day rating
    /// blocks chain their schedule from local midnight of this day, so the schedule stays stable (and the
    /// simulation walk stays short) until the next save re-anchors it. Stamped server-side on every save.</summary>
    public DateTime ScheduleAnchorUtc { get; set; }


    /// <summary>Gets or sets the target output width. Every item is encoded to this width (height derived from a 16:9 frame) so the single continuous stream stays one uniform format across item boundaries.</summary>
    public int TranscodeWidth { get; set; } = 1280;

    /// <summary>Gets or sets the target video bitrate in kbps.</summary>
    public int TranscodeVideoBitrateKbps { get; set; } = 4000;

    /// <summary>Gets or sets the video codec family. The concrete encoder follows Jellyfin's hardware-acceleration configuration.</summary>
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;

    /// <summary>Gets or sets the audio codec.</summary>
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Aac;

    /// <summary>Gets or sets a value indicating whether to force software encoding and decoding for channel streams, ignoring Jellyfin's hardware acceleration. Slower, but universally compatible across systems, codecs, and media types.</summary>
    public bool DisableHardwareAcceleration { get; set; }

    /// <summary>Gets or sets the viewer's native language, as a three-letter ISO code (e.g. <c>eng</c>). With a channel's subtitle rule set to Forced only, content whose default audio track is not this language burns in subtitles so foreign-language content stays followable.</summary>
    public string DefaultSubtitleLanguage { get; set; } = "eng";

    /// <summary>Gets or sets how many seconds of a channel are encoded and buffered before playback is handed to the player. A larger cushion rides out the first seconds of a tune-in (probe, player start-up, and the first item boundary) without stuttering, at the cost of waiting slightly longer before the picture appears. The producer always stays at least this far ahead of the viewer.</summary>
    public int StartupBufferSeconds { get; set; } = 12;

    /// <summary>Gets or sets the font burned-in subtitles are rendered in, by family name (e.g. <c>Arial</c>). Empty uses the subtitle file's own font, falling back to the renderer's default.</summary>
    public string SubtitleFont { get; set; } = string.Empty;

    /// <summary>Gets or sets the burned-in subtitle size as a percentage of normal. 100 leaves the subtitle at the size it was authored at.</summary>
    public int SubtitleFontScalePercent { get; set; } = 100;

    /// <summary>Gets or sets the burned-in subtitle text colour as <c>#RRGGBB</c>. Empty keeps the subtitle file's own colour.</summary>
    public string SubtitleTextColor { get; set; } = string.Empty;

    /// <summary>Gets or sets the burned-in subtitle outline (or box) colour as <c>#RRGGBB</c>. Empty keeps the subtitle file's own colour.</summary>
    public string SubtitleOutlineColor { get; set; } = string.Empty;

    /// <summary>Gets or sets how burned-in subtitles are separated from the picture: the subtitle file's own style, an outline, or a solid box behind the text.</summary>
    public SubtitleBorderStyle SubtitleBorder { get; set; } = SubtitleBorderStyle.Default;

    /// <summary>Gets or sets a value indicating whether burned-in subtitles are drawn in bold.</summary>
    public bool SubtitleBold { get; set; }

    /// <summary>Gets or sets the directory each channel's live playlist and its rolling stream segments are written to while playing. Empty (the default) uses a <c>livechannels</c> folder inside Jellyfin's cache. Only a short rolling window of segments is kept on disk at a time, so this stays small regardless of how long the channel runs.</summary>
    public string StreamDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of channel streams that may encode at once. When a new tune-in would exceed this, the oldest stream nobody is watching is closed to make room. This bounds CPU use when a client never sends the close (so abandoned sessions cannot pile up). Streams that are actively being watched are never closed for it, so the count can run over when everything is in use. Zero means unlimited.</summary>
    public int MaxConcurrentSessions { get; set; } = 3;

    /// <summary>Gets or sets the maximum number of minutes any one channel stream may encode before it is closed automatically. A backstop for clients that never send the close on stop, so a stream that is still being watched is left alone and closed once its viewer stops. Zero turns the limit off.</summary>
    public int SessionTimeoutMinutes { get; set; }

    /// <summary>Gets or sets the built-in "Popular" channel's settings. It always lives at channel 0 and draws its content from the recent, top-rated, and most-watched movies and shows on the server, so its number and content are fixed; everything else (name, icon, rating band, subtitle rule, loop behaviour, and whether it is enabled) is configurable here.</summary>
    public Channel PopularChannel { get; set; } = new()
    {
        Name = "Popular",
        Number = 0,
        LogoStyle = LogoStyle.Symbol,
        LogoSymbol = "diversity_1",
        LogoShowName = true,
        Enabled = true,
        Shuffle = true,
        ShuffleEpisodes = false,
        EpisodesPerBlock = 4,
        IncludeUnrated = true
    };

    /// <summary>
    /// The start-up buffer actually used, clamping a value stored by an older version (or by an API client) into
    /// the workable range: too small and the player joins on the encoder's heels, too large and the wait before
    /// the picture appears outgrows the rolling window on disk.
    /// </summary>
    /// <returns>The buffer in seconds.</returns>
    public int EffectiveStartupBufferSeconds()
        => StartupBufferSeconds <= 0
            ? DefaultStartupBufferSeconds
            : Math.Clamp(StartupBufferSeconds, MinStartupBufferSeconds, MaxStartupBufferSeconds);
}
