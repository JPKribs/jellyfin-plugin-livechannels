using System;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// A snapshot of one active channel stream for the configuration page's Sessions tab.
/// </summary>
/// <param name="Id">The live stream id, used to close the session.</param>
/// <param name="Number">The channel number.</param>
/// <param name="Name">The channel name.</param>
/// <param name="StartedUtc">When the stream started, in UTC. The configuration page derives the run time from this.</param>
/// <param name="Speed">The latest encode speed as a realtime multiple (1.0 is realtime) zero until ffmpeg reports one.</param>
/// <param name="StopsInSeconds">Seconds until a viewer-closed session is torn down (it lingers briefly so a
/// returning viewer re-tunes instantly), or <c>null</c> while the stream still has a viewer.</param>
public sealed record ActiveSession(string Id, int Number, string Name, DateTime StartedUtc, double Speed, int? StopsInSeconds = null);
