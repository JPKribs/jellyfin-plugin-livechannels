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

    /// <summary>Gets or sets a value indicating whether the continuous (concat) pipeline may stream into a zero-disk
    /// named pipe instead of a buffered file. Off by default: a non-seekable pipe makes some servers (notably
    /// Intel QSV) probe the live stream as interlaced and insert a deinterlace pass that fails, whereas the
    /// buffered file is probed correctly. Enabling it trades that reliability for no temp-file growth.</summary>
    public bool EnableConcatPipe { get; set; }
}
