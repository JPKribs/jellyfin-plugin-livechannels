using System;
using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveChannels.Api;

/// <summary>
/// Serves the channels to Jellyfin as an M3U tuner: an M3U playlist, an XMLTV guide, and a continuous MPEG-TS
/// stream per channel. These run on Jellyfin's own web server but only answer loopback (127.0.0.1) requests, so
/// Jellyfin can reach them on the same host while they stay unreachable from outside even if the Jellyfin port is
/// forwarded. Jellyfin consumes the stream over HTTP, reading it sequentially and direct-streaming it, instead of
/// re-probing a growing temp file (which misreads the progressive stream as interlaced and forces a re-encode).
/// </summary>
[ApiController]
[Route("livechannels")]
public class LiveChannelsController : ControllerBase
{
    private readonly EncoderResolver _encoders;
    private readonly ChannelService _channels;
    private readonly StreamSessionService _streams;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsController"/> class.
    /// </summary>
    /// <param name="encoders">The encoder resolver, used to report the active hardware acceleration.</param>
    /// <param name="channels">The channel service, used to resolve channels for the tuner.</param>
    /// <param name="streams">The stream session service, used to produce each channel's feed.</param>
    public LiveChannelsController(EncoderResolver encoders, ChannelService channels, StreamSessionService streams)
    {
        _encoders = encoders;
        _channels = channels;
        _streams = streams;
    }

    /// <summary>
    /// Reports the hardware acceleration the channel encoder will use, as configured in Jellyfin's Playback
    /// settings. Used by the settings page to confirm whether streams are hardware-accelerated.
    /// </summary>
    /// <returns>A small JSON object with the acceleration label and whether it is hardware.</returns>
    [HttpGet("encoders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Encoders()
    {
        var (label, hardware) = _encoders.DescribeAcceleration();
        return new JsonResult(new { acceleration = label, hardware });
    }

    /// <summary>
    /// An M3U tuner playlist of the enabled channels. Registered automatically as a Jellyfin M3U tuner. Loopback only.
    /// </summary>
    /// <returns>An <c>#EXTM3U</c> playlist, or 403 from a non-loopback caller.</returns>
    [HttpGet("tuner.m3u")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult Tuner()
    {
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var baseUrl = Request.Scheme + "://" + Request.Host.Value;
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var channel in _channels.GetEnabledChannels())
        {
            sb.Append("#EXTINF:-1 tvg-id=\"")
              .Append(channel.Id)
              .Append("\" tvg-chno=\"")
              .Append(channel.Number.ToString(CultureInfo.InvariantCulture))
              .Append("\" tvg-logo=\"")
              .Append(baseUrl)
              .Append("/livechannels/logo/")
              .Append(channel.Id)
              .Append("\",")
              .Append(channel.Name)
              .Append('\n')
              .Append(baseUrl)
              .Append("/livechannels/stream/")
              .Append(channel.Id)
              .Append('\n');
        }

        return Content(sb.ToString(), "application/x-mpegurl");
    }

    /// <summary>
    /// An XMLTV guide for the enabled channels, paired with the M3U tuner. The channel ids match the M3U
    /// <c>tvg-id</c> values so Jellyfin maps listings to channels. Loopback only.
    /// </summary>
    /// <returns>An XMLTV document, or 403 from a non-loopback caller.</returns>
    [HttpGet("guide.xml")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult Guide()
    {
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var now = DateTime.UtcNow;
        var end = now.AddHours(48);
        var channels = _channels.GetEnabledChannels();
        var baseUrl = Request.Scheme + "://" + Request.Host.Value;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"Live Channels\">\n");

        foreach (var channel in channels)
        {
            sb.Append("  <channel id=\"").Append(Esc(channel.Id)).Append("\"><display-name>")
              .Append(Esc(channel.Name)).Append("</display-name>")
              .Append("<icon src=\"").Append(Esc(baseUrl + "/livechannels/logo/" + channel.Id)).Append("\" />")
              .Append("</channel>\n");
        }

        foreach (var channel in channels)
        {
            var programs = _channels.ResolvePrograms(channel);
            if (programs.Count == 0)
            {
                continue;
            }

            foreach (var slot in ScheduleCalculator.BuildSchedule(programs, now, end, ScheduleCalculator.Epoch))
            {
                var p = slot.Program;
                sb.Append("  <programme start=\"").Append(Xmltv(slot.Start)).Append("\" stop=\"")
                  .Append(Xmltv(slot.Stop)).Append("\" channel=\"").Append(Esc(channel.Id)).Append("\">\n")
                  .Append("    <title>").Append(Esc(p.Title)).Append("</title>\n");

                if (!string.IsNullOrEmpty(p.Overview))
                {
                    sb.Append("    <desc>").Append(Esc(p.Overview)).Append("</desc>\n");
                }

                foreach (var genre in p.Genres)
                {
                    sb.Append("    <category>").Append(Esc(genre)).Append("</category>\n");
                }

                if (p.IsMovie)
                {
                    sb.Append("    <category>Movie</category>\n");
                }

                if (p.IsKids)
                {
                    sb.Append("    <category>Kids</category>\n");
                }

                if (p.SeasonNumber is int season && p.EpisodeNumber is int episode)
                {
                    sb.Append("    <episode-num system=\"xmltv_ns\">")
                      .Append((season - 1).ToString(CultureInfo.InvariantCulture)).Append('.')
                      .Append((episode - 1).ToString(CultureInfo.InvariantCulture)).Append(".0</episode-num>\n");
                }

                if (p.Year is int year)
                {
                    sb.Append("    <date>").Append(year.ToString(CultureInfo.InvariantCulture)).Append("</date>\n");
                }

                if (!string.IsNullOrEmpty(p.OfficialRating))
                {
                    sb.Append("    <rating><value>").Append(Esc(p.OfficialRating)).Append("</value></rating>\n");
                }

                if (p.HasPrimaryImage)
                {
                    sb.Append("    <icon src=\"")
                      .Append(Esc(baseUrl + "/livechannels/programimage/" + p.ItemId.ToString("N", CultureInfo.InvariantCulture)))
                      .Append("\" />\n");
                }

                sb.Append("  </programme>\n");
            }
        }

        sb.Append("</tv>\n");
        return Content(sb.ToString(), "application/xml");
    }

    /// <summary>
    /// Streams a channel as a continuous MPEG-TS over HTTP for the M3U tuner, writing the producer's feed straight
    /// to the response until Jellyfin disconnects. Loopback only.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">Bound to the request abort, so the producer stops when Jellyfin disconnects.</param>
    /// <returns>A task that completes when streaming stops.</returns>
    [HttpGet("stream/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task StreamChannel(string channelId, CancellationToken cancellationToken)
    {
        if (!IsAllowed())
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var channel = _channels.FindChannel(channelId);
        if (channel is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "video/mp2t";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await _streams.StreamToAsync(channel, Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected; expected.
        }
    }

    /// <summary>
    /// Serves a channel's logo (the uploaded image or the generated square) for the M3U <c>tvg-logo</c> and the
    /// guide channel icon. Loopback only by default.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The logo image, or 404 when there is none.</returns>
    [HttpGet("logo/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Logo(string channelId, CancellationToken cancellationToken)
    {
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var channel = _channels.FindChannel(channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var logo = await _channels.GetChannelLogoAsync(channel, cancellationToken).ConfigureAwait(false);
        return logo is null ? NotFound() : File(logo.Value.Bytes, logo.Value.ContentType);
    }

    /// <summary>
    /// Serves a program's primary image (episode/movie artwork) for the guide programme icon. Loopback only by default.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The image file, or 404 when there is none.</returns>
    [HttpGet("programimage/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "The path is resolved by Jellyfin's library from a parsed item GUID, not built from the request string.")]
    public ActionResult ProgramImage(string itemId)
    {
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!Guid.TryParse(itemId, out var id))
        {
            return NotFound();
        }

        var path = _channels.GetItemImagePath(id);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, ContentTypeFor(path));
    }

    // Best-effort image content type from the file extension; defaults to JPEG, which Jellyfin item images use.
    private static string ContentTypeFor(string path)
        => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };

    // The tuner endpoints carry no Jellyfin auth (Jellyfin's tuner host fetches them anonymously), so by default
    // they only answer loopback (127.0.0.1) requests: Jellyfin reaches them on the same host, and a forwarded
    // Jellyfin port cannot. Enabling external access opens them to other devices (unauthenticated).
    private bool IsAllowed()
        => (Plugin.Instance?.ReadConfiguration(c => c.AllowExternalTunerAccess) ?? false)
        || (HttpContext.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip));

    private static string Xmltv(DateTime utc)
        => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private static string Esc(string? value)
        => SecurityElement.Escape(value) ?? string.Empty;
}
