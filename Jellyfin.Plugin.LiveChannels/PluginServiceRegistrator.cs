using Jellyfin.Plugin.LiveChannels.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

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

        // Register the Live TV service as a concrete singleton and alias ILiveTvService to it, so Jellyfin
        // discovers the channels in-process and the cleanup scheduled task shares the exact same instance (and
        // therefore its live-session state and stream directory).
        serviceCollection.AddSingleton<LiveChannelsTvService>();
        serviceCollection.AddSingleton<ILiveTvService>(sp => sp.GetRequiredService<LiveChannelsTvService>());
        serviceCollection.AddSingleton<IScheduledTask, StreamCleanupTask>();
    }
}
