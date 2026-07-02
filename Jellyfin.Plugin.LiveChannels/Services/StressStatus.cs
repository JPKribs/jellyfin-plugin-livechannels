using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// A snapshot of the encoder stress test for the settings page.
/// </summary>
/// <param name="Running">Whether a test is currently running.</param>
/// <param name="CurrentStreams">The round in progress (how many concurrent streams), or zero when idle.</param>
/// <param name="Rounds">The completed rounds so far.</param>
/// <param name="Recommended">The recommended maximum concurrent streams once the test finishes, or <c>null</c> while running.</param>
/// <param name="Error">Why the test stopped early, or <c>null</c>.</param>
/// <param name="ItemName">The item the test encodes, for display.</param>
public sealed record StressStatus(bool Running, int CurrentStreams, IReadOnlyList<StressRound> Rounds, int? Recommended, string? Error, string? ItemName);
