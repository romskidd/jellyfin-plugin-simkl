using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Simkl.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Simkl
{
    /// <summary>
    /// SIMKL tracker.
    /// </summary>
    public class SimklPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SimklPlugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
        public SimklPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the current instance of the plugin.
        /// </summary>
        public static SimklPlugin? Instance { get; private set; }

        /// <inheritdoc />
        public override Guid Id => new Guid("03A7C840-6154-471F-8BE3-856CDC26D500");

        /// <inheritdoc />
        public override string Name => "RK Simkl Scrobbler";

        /// <inheritdoc />
        public override string Description => "Scrobble your watched Movies, TV Shows and Anime to Simkl and share your progress with friends!";

        /// <inheritdoc />
        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            // The admin page links an account by saving the whole configuration
            // with the new token in it. What was cached about the previous
            // account must not survive that.
            if (configuration is PluginConfiguration incoming)
            {
                foreach (var next in incoming.UserConfigs)
                {
                    var current = Configuration.GetByGuid(next.Id);
                    if (current != null
                        && !string.IsNullOrEmpty(next.UserToken)
                        && !string.Equals(current.UserToken, next.UserToken, StringComparison.Ordinal))
                    {
                        next.ForgetCachedAccount();
                    }
                }
            }

            base.UpdateConfiguration(configuration);
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",

                // Show the plugin in the dashboard's left sidebar instead of
                // hiding it behind the plugin catalogue.
                EnableInMainMenu = true,
                DisplayName = "RK Simkl Scrobbler",
                MenuIcon = "sync"
            };
        }
    }
}