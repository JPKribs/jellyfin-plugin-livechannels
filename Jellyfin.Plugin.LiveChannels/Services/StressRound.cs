namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// One completed round of the encoder stress test.
/// </summary>
/// <param name="Streams">How many concurrent streams this round ran.</param>
/// <param name="MinFps">The slowest stream's frame rate; 30 fps is exactly realtime for the channel output.</param>
/// <param name="Passed">Whether every stream held the realtime bar.</param>
public sealed record StressRound(int Streams, double MinFps, bool Passed);
