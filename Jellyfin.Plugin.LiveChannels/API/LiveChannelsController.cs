using Jellyfin.Plugin.LiveChannels.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveChannels.Api;

/// <summary>
/// A small authenticated helper for the configuration page. All channel content (channels, guide, streams,
/// and logos) is served in-process through Jellyfin's Live TV; this controller exposes no content endpoints.
/// </summary>
[ApiController]
[Route("livechannels")]
public class LiveChannelsController : ControllerBase
{
    private readonly EncoderResolver _encoders;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveChannelsController"/> class.
    /// </summary>
    /// <param name="encoders">The encoder resolver, used to report the active hardware acceleration.</param>
    public LiveChannelsController(EncoderResolver encoders)
    {
        _encoders = encoders;
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
}
