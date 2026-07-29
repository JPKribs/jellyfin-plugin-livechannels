using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: picking and extracting the burn-in subtitle for a program.
public partial class ChannelService
{
    // Item/track keys whose subtitle extraction is currently running, so concurrent tune-ins don't pile a
    // second whole-file extraction onto the producer's critical path.
    private readonly ConcurrentDictionary<string, byte> _subtitleExtractions = new(StringComparer.Ordinal);

    /// <summary>
    /// Picks the subtitle track to burn into an item for the given mode. <see cref="SubtitleBurnInMode.Forced"/>
    /// burns only the forced track, except when the default audio track is in a known non-English language, where
    /// it behaves like <see cref="SubtitleBurnInMode.Always"/> so foreign-language content stays followable.
    /// <see cref="SubtitleBurnInMode.Always"/> burns the forced track when present, otherwise the default or first.
    /// </summary>
    /// <param name="program">The program, carrying its subtitle streams and default-audio language probed at refresh.</param>
    /// <param name="mode">The channel's subtitle burn-in mode.</param>
    /// <returns>The chosen subtitle's index among the item's subtitle streams and whether it is text-based, or <c>null</c> when nothing should be burned in.</returns>
    public static (int RelativeIndex, bool IsText)? FindBurnInSubtitle(ProgramEntry program, SubtitleBurnInMode mode)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (mode == SubtitleBurnInMode.Never)
        {
            return null;
        }

        // An external bitmap subtitle (a .sup beside the media) is the one kind nothing can burn: it is a picture,
        // so it has to be composited from a mapped stream, and being external it has no stream inside the media
        // file to map. Dropping it here lets another track be chosen instead of the channel silently showing none.
        var subtitles = program.Subtitles.Where(s => s.IsText || !s.IsExternal).ToList();

        // Forced always wins when present, in either mode.
        for (var i = 0; i < subtitles.Count; i++)
        {
            if (subtitles[i].IsForced)
            {
                return (subtitles[i].RelativeIndex, subtitles[i].IsText);
            }
        }

        // "Forced only" also burns subtitles when the audio we play is not in the configured default language, so
        // foreign-language content stays followable. Audio in the default language, or untagged audio, shows
        // nothing without a forced track, so ordinary content is never subtitled by surprise.
        var defaultLanguage = Plugin.Instance?.ReadConfiguration(c => c.DefaultSubtitleLanguage) ?? string.Empty;
        var audioLanguage = program.DefaultAudioLanguage;
        var forcedForForeignAudio = mode == SubtitleBurnInMode.Forced
            && !string.IsNullOrEmpty(audioLanguage)
            && !string.IsNullOrEmpty(defaultLanguage)
            && !string.Equals(audioLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase);

        if ((mode == SubtitleBurnInMode.Always || forcedForForeignAudio) && subtitles.Count > 0)
        {
            // Both "Always" and non-English-audio "Forced only" burn the same track "Always" picks: the default
            // subtitle, or the first one when none is flagged default.
            var chosen = 0;
            for (var i = 0; i < subtitles.Count; i++)
            {
                if (subtitles[i].IsDefault)
                {
                    chosen = i;
                    break;
                }
            }

            return (subtitles[chosen].RelativeIndex, subtitles[chosen].IsText);
        }

        return null;
    }

    /// <summary>
    /// Resolves the chosen embedded text subtitle to a burn-ready file the same way Jellyfin's own transcodes do:
    /// <see cref="ISubtitleEncoder.GetSubtitleFilePath"/> extracts the track once into Jellyfin's subtitle cache
    /// (shared with normal playback, so it is usually already warm) and returns that file. Burning the extracted
    /// file rather than the media file means libass reads a few kilobytes instead of scanning gigabytes to reach a
    /// deep tune-in point, and the file keeps the markup the track was authored with, so bold, italic, and colour
    /// tags survive into the picture.
    /// <para>
    /// Bounded by a short timeout, because this sits on the producer's critical path: a cold extraction keeps
    /// running in the background to warm the cache while the caller falls back for this one item.
    /// </para>
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="relativeIndex">The chosen subtitle's index among the item's subtitle streams.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subtitle file and any attached-font directory, or <c>null</c> when it was not ready in time.</returns>
    public async Task<BurnInSubtitleFile?> TryResolveBurnInSubtitleAsync(Guid itemId, int relativeIndex, CancellationToken cancellationToken)
    {
        var key = itemId.ToString("N", CultureInfo.InvariantCulture) + "-" + relativeIndex.ToString(CultureInfo.InvariantCulture);

        // Only one extraction per item/track at a time. A concurrent tune-in skips rather than launching a
        // duplicate; it simply starts without a burned-in subtitle, as a cold tune-in already does.
        if (!_subtitleExtractions.TryAdd(key, 0))
        {
            return null;
        }

        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return null;
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            var source = sources.Count > 0 ? sources[0] : null;
            if (source?.MediaStreams is null)
            {
                return null;
            }

            var subtitles = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
            if (relativeIndex < 0 || relativeIndex >= subtitles.Count)
            {
                return null;
            }

            // CancellationToken.None so a cold extraction still finishes caching even after we stop waiting for
            // it below; the next airing of this item then burns it immediately.
            var resolve = _subtitleEncoder.GetSubtitleFilePath(subtitles[relativeIndex], source, CancellationToken.None);
            var ready = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken)).ConfigureAwait(false);
            if (ready != resolve || resolve.Status != TaskStatus.RanToCompletion)
            {
                _logger.LogDebug("Live Channels: subtitle extraction for {ItemId} is still warming; burning from the media file instead", itemId);
                return null;
            }

            var path = await resolve.ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            return new BurnInSubtitleFile(path, AttachmentFontsDirectory(source.Id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not prepare a burn-in subtitle for {ItemId}", itemId);
            return null;
        }
        finally
        {
            _subtitleExtractions.TryRemove(key, out _);
        }
    }

    // The fonts Jellyfin extracted from the media's attachments, so an ASS subtitle authored against an embedded
    // font renders in it rather than a substitute. Null when the item carries none.
    private string? AttachmentFontsDirectory(string mediaSourceId)
    {
        try
        {
            var directory = _pathManager.GetAttachmentFolderPath(mediaSourceId);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the attachment font directory for {MediaSourceId}", mediaSourceId);
            return null;
        }
    }
}

/// <summary>
/// A burn-ready subtitle: the extracted subtitle file plus, when the media carries attached fonts, the directory
/// libass should load them from.
/// </summary>
/// <param name="Path">The subtitle file to burn.</param>
/// <param name="FontsDirectory">The attached-font directory, or <c>null</c>.</param>
public sealed record BurnInSubtitleFile(string Path, string? FontsDirectory);
