using System;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Watches a producer's MPEG-TS output as it streams past and records where its picture and sound actually end,
/// so the next producer can start exactly there. ffmpeg's own <c>-progress</c> position follows the output's
/// decode timestamps, which trail the last displayed frame by the encoder's frame-reorder delay (two frames for
/// libx264, other amounts for hardware encoders, none for some), and says nothing about the audio track. Reading
/// the presentation timestamps off the packet headers is exact for every encoder. Only the PES headers are
/// looked at (a few bytes per 188-byte packet), so it costs next to nothing on the pump path.
/// </summary>
public sealed class TsEndScanner
{
    private const int PacketSize = 188;
    private const byte SyncByte = 0x47;

    // PTS is a 33-bit count of a 90 kHz clock, so it wraps every 2^33 / 90000 seconds (about 26.5 hours).
    private const double WrapSeconds = 8589934592.0 / 90000.0;

    private readonly byte[] _partial = new byte[PacketSize];
    private int _partialLength;

    /// <summary>Gets the latest video presentation timestamp seen, in seconds (unwrapped against the first one), or <c>null</c>.</summary>
    public double? MaxVideoPts { get; private set; }

    /// <summary>Gets the latest audio presentation timestamp seen, in seconds (unwrapped against the first one), or <c>null</c>.</summary>
    public double? LastAudioPts { get; private set; }

    /// <summary>Gets the duration of one audio frame, from the spacing of the last two audio timestamps, or <c>null</c>.</summary>
    public double? AudioFrameSeconds { get; private set; }

    /// <summary>Gets the earliest video presentation timestamp seen (where the picture starts), or <c>null</c>.</summary>
    public double? MinVideoPts { get; private set; }

    /// <summary>Gets the first audio presentation timestamp seen (where the sound starts), or <c>null</c>.</summary>
    public double? FirstAudioPts { get; private set; }

    /// <summary>
    /// Gets how far this output's sound starts ahead of its picture: the encoder's priming (about 21 ms for AAC,
    /// 5 ms for AC-3). The next producer runs the same encoder, so its sound starts that far ahead of its picture
    /// too. Zero when the sound starts with or after the picture, and never more than two audio frames.
    /// </summary>
    public double AudioLeadSeconds
        => MinVideoPts is { } video && FirstAudioPts is { } audio && AudioFrameSeconds is { } frame
            ? Math.Clamp(video - audio, 0, 2 * frame)
            : 0;

    /// <summary>
    /// Feeds the next chunk of the stream. Chunks may split packets anywhere.
    /// </summary>
    /// <param name="data">The bytes, in stream order.</param>
    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_partialLength > 0)
        {
            var take = Math.Min(PacketSize - _partialLength, data.Length);
            data[..take].CopyTo(_partial.AsSpan(_partialLength));
            _partialLength += take;
            data = data[take..];
            if (_partialLength < PacketSize)
            {
                return;
            }

            ScanPacket(_partial);
            _partialLength = 0;
        }

        while (data.Length >= PacketSize)
        {
            if (data[0] != SyncByte)
            {
                // Out of step (never expected from ffmpeg's muxer): find the next sync byte and carry on.
                var next = data[1..].IndexOf(SyncByte);
                if (next < 0)
                {
                    return;
                }

                data = data[(next + 1)..];
                continue;
            }

            ScanPacket(data[..PacketSize]);
            data = data[PacketSize..];
        }

        data.CopyTo(_partial);
        _partialLength = data.Length;
    }

    /// <summary>
    /// Where the next producer should start on the channel timeline so that neither of its tracks overlaps this
    /// output: after this output's last video frame, and after its last audio frame plus the room the next
    /// encoder's priming needs (its sound starts <see cref="AudioLeadSeconds"/> ahead of its picture).
    /// </summary>
    /// <param name="frameSeconds">The duration of one output video frame.</param>
    /// <param name="reference">An approximate position on the channel timeline (the producer's own progress
    /// reading), used only to place the 33-bit timestamps on the right lap of a long-running channel.</param>
    /// <returns>The start position for the next producer, or <c>null</c> when no video timestamp was seen.</returns>
    public double? NextStartSeconds(double frameSeconds, double reference)
    {
        if (MaxVideoPts is not { } video)
        {
            return null;
        }

        var end = video + frameSeconds;
        if (LastAudioPts is { } audio && AudioFrameSeconds is { } frame)
        {
            end = Math.Max(end, audio + frame + AudioLeadSeconds);
        }

        // Place the raw timestamp on the lap nearest the reference.
        var laps = Math.Round((reference - end) / WrapSeconds);
        return end + (laps * WrapSeconds);
    }

    // Unwraps a raw timestamp to the lap of the value it follows, so a wrap mid-output reads as forward motion.
    private static double Unwrap(double raw, double? previous)
    {
        if (previous is not { } prior)
        {
            return raw;
        }

        var laps = Math.Round((prior - raw) / WrapSeconds);
        return raw + (laps * WrapSeconds);
    }

    private void ScanPacket(ReadOnlySpan<byte> packet)
    {
        // Only a packet that starts a PES packet carries its header (payload_unit_start_indicator).
        if ((packet[1] & 0x40) == 0)
        {
            return;
        }

        var adaptation = (packet[3] >> 4) & 0x3;
        if ((adaptation & 0x1) == 0)
        {
            return;
        }

        var offset = 4;
        if ((adaptation & 0x2) != 0)
        {
            offset += 1 + packet[4];
        }

        // PES header: start code, stream id, length, two flag bytes, header length, then the PTS (5 bytes).
        if (offset + 14 > packet.Length)
        {
            return;
        }

        var pes = packet[offset..];
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1 || (pes[7] & 0x80) == 0)
        {
            return;
        }

        var streamId = pes[3];
        var pts = ((long)((pes[9] >> 1) & 0x07) << 30)
            | ((long)pes[10] << 22)
            | ((long)(pes[11] >> 1) << 15)
            | ((long)pes[12] << 7)
            | ((long)pes[13] >> 1);
        var seconds = pts / 90000.0;

        if (streamId >= 0xE0 && streamId <= 0xEF)
        {
            // Video timestamps arrive in decode order, so the latest frame is the largest, not the last.
            // The first timestamp of each track is placed on the other track's lap, so a producer starting right
            // at the 33-bit wrap cannot put its picture and sound a lap apart.
            var value = Unwrap(seconds, MaxVideoPts ?? LastAudioPts);
            MaxVideoPts = MaxVideoPts is { } max ? Math.Max(max, value) : value;
            MinVideoPts = MinVideoPts is { } min ? Math.Min(min, value) : value;
        }
        else if ((streamId >= 0xC0 && streamId <= 0xDF) || streamId == 0xBD)
        {
            // MPEG/AAC audio uses 0xC0-0xDF; AC-3 and E-AC-3 travel as private stream 1 (0xBD).
            var value = Unwrap(seconds, LastAudioPts ?? MaxVideoPts);
            FirstAudioPts ??= value;
            if (LastAudioPts is { } last && value > last)
            {
                AudioFrameSeconds = value - last;
            }

            LastAudioPts = value;
        }
    }
}
