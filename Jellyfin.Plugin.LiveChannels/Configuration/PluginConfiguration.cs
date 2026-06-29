using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveChannels.Configuration;

/// <summary>
/// Single configuration object for the plugin. XML-serialized by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the configured virtual channels.</summary>
    public List<Channel> Channels { get; set; } = new();


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

    /// <summary>Gets or sets how many seconds the client buffers before playback starts. A larger buffer rides out hiccups on tune-in at the cost of a longer wait before the picture appears.</summary>
    public int BufferSeconds { get; set; } = 3;

    /// <summary>Gets or sets the directory each channel's live playlist and its rolling stream segments are written to while playing. Empty (the default) uses a <c>livechannels</c> folder inside Jellyfin's cache. Only a short rolling window of segments is kept on disk at a time, so this stays small regardless of how long the channel runs.</summary>
    public string StreamDirectory { get; set; } = string.Empty;

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
        IncludeUnrated = true,
        KidsRatingThreshold = "G"
    };
}
