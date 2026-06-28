using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.LiveChannels;

/// <summary>
/// Registers plugin services with the Jellyfin DI container. The Live TV service is registered as an
/// <see cref="ILiveTvService"/> so Jellyfin discovers the virtual channels in-process, with no HTTP endpoints.
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
        serviceCollection.AddSingleton<ActivityLogger>();
        serviceCollection.AddSingleton<ILiveTvService, LiveChannelsTvService>();
        serviceCollection.AddHostedService<TunerRegistrar>();
    }
}
