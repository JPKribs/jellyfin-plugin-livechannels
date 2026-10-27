using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// The continuous concat pipeline decodes every item with decoders set up for the first one, so a channel only
/// takes it when all its items share their formats; anything else airs item by item.
/// </summary>
public class ConcatCompatibilityTests
{
    private static ProgramEntry Item(string? video = "h264", string? audio = "aac", int? channels = 2, int? audioOrdinal = 0)
        => new(Guid.NewGuid(), "Item", null, TimeSpan.FromMinutes(30).Ticks, "/m.mkv")
        {
            SourceVideoCodec = video,
            SourceAudioCodec = audio,
            SourceAudioChannels = channels,
            DefaultAudioOrdinal = audioOrdinal
        };

    [Fact]
    public void MatchingItems_AreCompatible()
        => Assert.True(StreamSessionService.ConcatCompatible(new[] { Item(), Item(), Item(video: "H264", audio: "AAC") }));

    [Theory]
    [InlineData("hevc", "aac", 2, 0)] // video codec differs
    [InlineData("h264", "ac3", 2, 0)] // audio codec differs
    [InlineData("h264", "aac", 6, 0)] // channel count differs
    [InlineData("h264", "aac", 2, 1)] // one item plays its second track, the others their first
    [InlineData(null, "aac", 2, 0)] // formats never recorded (a schedule cached before they were)
    [InlineData("h264", null, null, 0)]
    public void AnyDifference_IsIncompatible(string? video, string? audio, int? channels, int? ordinal)
        => Assert.False(StreamSessionService.ConcatCompatible(new[] { Item(), Item(video, audio, channels, ordinal), Item() }));

    [Fact]
    public void ItemsNeedingDifferentColourConversions_AreIncompatible()
    {
        // One scale pass converts every concat item from a single input matrix, so an SD (bt601) item cannot
        // share a playlist with an HD (bt709) one.
        var hd = Item();
        var sd = Item();
        hd = new ProgramEntry(hd.ItemId, "HD", null, hd.DurationTicks, "/hd.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, DefaultAudioOrdinal = 0, SourceHeight = 1080, SourceColorSpace = "bt709" };
        sd = new ProgramEntry(sd.ItemId, "SD", null, sd.DurationTicks, "/sd.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, DefaultAudioOrdinal = 0, SourceHeight = 480 };
        Assert.False(StreamSessionService.ConcatCompatible(new[] { hd, sd }));

        // An untagged HD item reads as bt709, so it can share with a tagged one.
        var hdUntagged = new ProgramEntry(Guid.NewGuid(), "HD2", null, hd.DurationTicks, "/hd2.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, DefaultAudioOrdinal = 0, SourceHeight = 1080 };
        Assert.True(StreamSessionService.ConcatCompatible(new[] { hd, hdUntagged }));
    }

    [Fact]
    public void ConcatSuits_RejectsWhatOnlyThePerItemPathPlays()
    {
        Assert.True(StreamSessionService.ConcatSuits(new[] { Item(), Item() }));

        // A rebuild that brings in any of these must move a running channel off the concat path.
        var hdr = new ProgramEntry(Guid.NewGuid(), "HDR", null, TimeSpan.FromMinutes(30).Ticks, "/h.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, IsHdr = true };
        var uhd = new ProgramEntry(Guid.NewGuid(), "4K", null, TimeSpan.FromMinutes(30).Ticks, "/u.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, SourceHeight = 2160 };
        var silent = new ProgramEntry(Guid.NewGuid(), "Mute", null, TimeSpan.FromMinutes(30).Ticks, "/m.mkv") { SourceVideoCodec = "h264", SourceAudioCodec = "aac", SourceAudioChannels = 2, IsVideoOnly = true };
        Assert.False(StreamSessionService.ConcatSuits(new[] { Item(), hdr }));
        Assert.False(StreamSessionService.ConcatSuits(new[] { Item(), uhd }));
        Assert.False(StreamSessionService.ConcatSuits(new[] { Item(), silent }));
        Assert.False(StreamSessionService.ConcatSuits(new[] { Item(), Item(audio: "ac3") }));
    }

    [Fact]
    public void ItemsPlayingTheSameLaterTrack_AreCompatible()
        => Assert.True(StreamSessionService.ConcatCompatible(new[] { Item(audioOrdinal: 1), Item(audioOrdinal: 1) }));

    [Fact]
    public void AnUnrecordedFirstItem_IsIncompatible()
        => Assert.False(StreamSessionService.ConcatCompatible(new[] { Item(video: null), Item() }));
}
