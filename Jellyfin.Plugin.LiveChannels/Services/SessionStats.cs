using System;
using System.Threading;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Live, mutable statistics for one active channel stream. The producer writes to it as ffmpeg reports
/// progress and the configuration page's Sessions tab reads it, so the value is exchanged atomically
/// because the two run on different threads.
/// </summary>
public sealed class SessionStats
{
    private long _speedBits;

    /// <summary>
    /// Gets or sets the most recent encode speed as a realtime multiple, parsed from the producer ffmpeg's
    /// progress output. 1.0 is realtime; below that the box is not keeping up. Zero until ffmpeg reports one.
    /// </summary>
    public double Speed
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
        set => Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(value));
    }
}
