using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

/// <summary>
/// Registers (and keeps in sync) an M3U tuner and XMLTV guide in Jellyfin's Live TV that point back at this
/// plugin's own endpoints, so the channels are consumed over HTTP. That path is what lets Jellyfin read the
/// stream sequentially and direct-stream it, instead of re-probing a temp file and forcing a re-encode. Runs
/// once shortly after startup and again whenever the configuration is saved.
/// </summary>
public sealed class TunerRegistrar : IHostedService
{
    private const string FriendlyName = "Live Channels";

    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<TunerRegistrar> _logger;

    private static TunerRegistrar? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerRegistrar"/> class.
    /// </summary>
    /// <param name="tuners">Jellyfin's tuner host manager.</param>
    /// <param name="listings">Jellyfin's listings (guide) manager.</param>
    /// <param name="serverConfig">The server configuration, used to find an existing tuner/guide for an idempotent update.</param>
    /// <param name="logger">The logger.</param>
    public TunerRegistrar(ITunerHostManager tuners, IListingsManager listings, IServerConfigurationManager serverConfig, ILogger<TunerRegistrar> logger)
    {
        _tuners = tuners;
        _listings = listings;
        _serverConfig = serverConfig;
        _logger = logger;
        _instance = this;
    }

    /// <summary>Re-registers the tuner and guide; called when the plugin configuration is saved.</summary>
    public static void Reregister() => _instance?.Run();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Give Jellyfin's Live TV a moment to finish initializing before adding our tuner.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await RegisterAsync().ConfigureAwait(false);
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run() => _ = RegisterAsync();

    private async Task RegisterAsync()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            var baseUrl = (config?.TunerBaseUrl ?? "http://127.0.0.1:8096").TrimEnd('/');
            var bitrateMbps = Math.Max(1, (config?.TranscodeVideoBitrateKbps ?? 4000) / 1000);
            var m3uUrl = baseUrl + "/livechannels/tuner.m3u";
            var guideUrl = baseUrl + "/livechannels/guide.xml";

            // Remove every tuner and guide this plugin previously created before adding one of each, so repeated
            // saves (and changed URLs across versions) update in place and never accumulate duplicates.
            var liveTv = _serverConfig.GetConfiguration<LiveTvOptions>("livetv");
            var remainingTuners = liveTv.TunerHosts.Where(h => !IsOurs(h)).ToArray();
            var remainingListings = liveTv.ListingProviders.Where(l => !IsOurs(l)).ToArray();
            if (remainingTuners.Length != liveTv.TunerHosts.Length
                || remainingListings.Length != liveTv.ListingProviders.Length)
            {
                liveTv.TunerHosts = remainingTuners;
                liveTv.ListingProviders = remainingListings;
                _serverConfig.SaveConfiguration("livetv", liveTv);
            }

            var tuner = new TunerHostInfo
            {
                Id = string.Empty,
                Url = m3uUrl,
                Type = "m3u",
                FriendlyName = FriendlyName,
                TunerCount = 0,
                AllowHWTranscoding = true,
                AllowFmp4TranscodingContainer = false,
                AllowStreamSharing = true,
                EnableStreamLooping = false,
                IgnoreDts = false,
                ReadAtNativeFramerate = true,
                FallbackMaxStreamingBitrate = bitrateMbps,
                ImportFavoritesOnly = false,
                UserAgent = string.Empty
            };

            await _tuners.SaveTunerHost(tuner, false).ConfigureAwait(false);

            var listings = new ListingsProviderInfo
            {
                Id = string.Empty,
                Type = "xmltv",
                Path = guideUrl,
                EnableAllTuners = true,
                MovieCategories = new[] { "Movie" },
                KidsCategories = new[] { "Kids" },
                NewsCategories = Array.Empty<string>(),
                SportsCategories = Array.Empty<string>()
            };

            await _listings.SaveListingProvider(listings, false, false).ConfigureAwait(false);

            _logger.LogInformation("Live Channels: registered the M3U tuner and XMLTV guide at {BaseUrl}", baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live Channels: could not register the M3U tuner and XMLTV guide");
        }
    }

    // A tuner this plugin created: the Live Channels friendly name, or any tuner pointing at our M3U endpoint.
    private static bool IsOurs(TunerHostInfo host)
        => string.Equals(host.FriendlyName, FriendlyName, StringComparison.Ordinal)
        || (host.Url?.Contains("/livechannels/tuner.m3u", StringComparison.OrdinalIgnoreCase) ?? false);

    // A guide this plugin created: an XMLTV provider pointing at our guide endpoint.
    private static bool IsOurs(ListingsProviderInfo provider)
        => string.Equals(provider.Type, "xmltv", StringComparison.Ordinal)
        && (provider.Path?.Contains("/livechannels/guide.xml", StringComparison.OrdinalIgnoreCase) ?? false);
}
