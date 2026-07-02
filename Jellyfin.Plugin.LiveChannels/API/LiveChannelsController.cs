using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveChannels.Api;

/// <summary>
/// A small admin-only helper for the configuration page. All channel content (channels, guide, streams,
/// and logos) is served in-process through Jellyfin's Live TV; this controller exposes no content endpoints,
/// only the encoder check and the active-session list the configuration page reads. Every endpoint requires
/// an elevated (administrator) token, so a session can only be listed or closed from the dashboard.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("livechannels")]
public class LiveChannelsController : ControllerBase
{
    private readonly EncoderResolver _encoders;
    private readonly LiveChannelsTvService _tv;
    private readonly StressTestService _stress;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsController"/> class.
    /// </summary>
    /// <param name="encoders">The encoder resolver, used to report the active hardware acceleration.</param>
    /// <param name="tv">The Live TV service, which owns the active channel streams.</param>
    /// <param name="stress">The encoder stress test the settings page can run.</param>
    public LiveChannelsController(EncoderResolver encoders, LiveChannelsTvService tv, StressTestService stress)
    {
        _encoders = encoders;
        _tv = tv;
        _stress = stress;
    }

    /// <summary>
    /// Starts the encoder stress test against a library item. Refused while any channel is streaming: the test
    /// saturates the encoder deliberately, and live viewers would both skew the measurement and suffer for it.
    /// </summary>
    /// <param name="itemId">The library item to encode.</param>
    /// <returns>Accepted when started; conflict when busy or streams are active.</returns>
    [HttpPost("stresstest/{itemId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartStressTest(Guid itemId)
    {
        if (_tv.GetActiveSessions().Count > 0)
        {
            return Conflict("Stop the active channel streams first; the test needs the encoder to itself.");
        }

        var error = _stress.TryStart(itemId);
        return error is null ? Accepted() : Conflict(error);
    }

    /// <summary>Reports the stress test's progress and, once finished, its recommendation.</summary>
    /// <returns>The current stress test status.</returns>
    [HttpGet("stresstest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult StressTestStatus() => new JsonResult(_stress.GetStatus());

    /// <summary>Cancels a running stress test.</summary>
    /// <returns>No content.</returns>
    [HttpDelete("stresstest")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelStressTest()
    {
        _stress.Cancel();
        return NoContent();
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
    /// Lists every channel stream currently encoding, for the Sessions tab.
    /// </summary>
    /// <returns>The active sessions, ordered by channel number.</returns>
    [HttpGet("sessions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Sessions() => new JsonResult(_tv.GetActiveSessions());

    /// <summary>
    /// Returns the channel logo for an active session, so the Sessions tab can show it.
    /// </summary>
    /// <param name="id">The live stream id.</param>
    /// <returns>The logo image, or 404 when the session or its logo is gone.</returns>
    [HttpGet("sessions/{id}/logo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SessionLogo(string id)
    {
        // Match the request against the live sessions and use our own stored id, so the file path that reaches
        // disk comes from the plugin's logo directory rather than from the request.
        var match = FindSession(id);
        if (match is null)
        {
            return NotFound();
        }

        var path = _tv.GetSessionLogoPath(match.Id);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, ContentTypeFor(path));
    }

    /// <summary>
    /// Closes an active channel stream, stopping its encoder. Used by the Sessions tab's kill button.
    /// </summary>
    /// <param name="id">The live stream id to close.</param>
    /// <returns>No content once the stream has been asked to stop.</returns>
    [HttpDelete("sessions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CloseSession(string id)
    {
        // Kill by our own stored id (the request only selects which live session), so no request string reaches
        // the stream-directory delete. KillSession, not CloseLiveStream: a client close lingers the encoder for
        // a possible instant re-tune, but the dashboard kill is an explicit "free this encoder NOW".
        var match = FindSession(id);
        if (match is not null)
        {
            _tv.KillSession(match.Id);
        }

        return NoContent();
    }

    private ActiveSession? FindSession(string id)
    {
        foreach (var session in _tv.GetActiveSessions())
        {
            if (string.Equals(session.Id, id, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    private static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/webp";
        }

        return "image/png";
    }
}
