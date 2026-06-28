using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.LiveChannels;

/// <summary>
/// Registers plugin services with the Jellyfin DI container. The channels are exposed through a self
/// configuring M3U tuner and XMLTV guide (see <see cref="TunerRegistrar"/>) rather than an in-process Live TV
/// service, so Jellyfin consumes them over HTTP and direct-streams them.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChannelService>();
        serviceCollection.AddSingleton<EncoderResolver>();
        serviceCollection.AddSingleton<StreamSessionService>();
        serviceCollection.AddSingleton<DefaultLogoService>();
        serviceCollection.AddHostedService<TunerRegistrar>();
    }
}
