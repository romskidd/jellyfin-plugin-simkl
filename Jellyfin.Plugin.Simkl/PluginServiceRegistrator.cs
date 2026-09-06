using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Simkl
{
    /// <inheritdoc />
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<SimklApi>();
            serviceCollection.AddSingleton<LibraryFilter>();

            // The retry queue is both a hosted service and a dependency of the
            // scrobbler, so it is registered once and resolved for both roles.
            serviceCollection.AddSingleton<ScrobbleRetryQueue>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<ScrobbleRetryQueue>());
            serviceCollection.AddHostedService<PlaybackScrobbler>();
            serviceCollection.AddHostedService<UserDataSync>();
            serviceCollection.AddHostedService<PluginPagesRegistration>();
            serviceCollection.AddHostedService<LinkValidation>();
            serviceCollection.AddSingleton<SimklImportService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<SimklImportService>());
        }
    }
}
