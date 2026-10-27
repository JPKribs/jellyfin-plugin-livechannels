namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The plugin-wide output settings a session encodes with. Read once when the session starts and kept for its
/// life (see <see cref="Services.SessionStats.PinOutput"/>): the copy-mode segmenter and every viewer's player set
/// up their decoders for the first item, so a codec or size change saved mid-session would switch the stream
/// under them. A saved change applies from the next session.
/// </summary>
/// <param name="Width">The output width.</param>
/// <param name="Bitrate">The video bitrate, in kbps.</param>
/// <param name="VideoCodec">The video codec.</param>
/// <param name="AudioCodec">The audio codec.</param>
public sealed record OutputSettings(int Width, int Bitrate, VideoCodec VideoCodec, AudioCodec AudioCodec);
