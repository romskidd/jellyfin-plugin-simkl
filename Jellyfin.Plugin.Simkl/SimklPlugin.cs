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
            // The admin page posts back the whole configuration it loaded, which
            // may be minutes old. The fields below are written by the server
            // (Simkl account details, scrobble and sync state) and the current
            // values win over that copy; only what the page edits is taken from it.
            if (configuration is PluginConfiguration incoming)
            {
                foreach (var next in incoming.UserConfigs)
                {
                    var current = Configuration.GetByGuid(next.Id);
                    if (current == null)
                    {
                        continue;
                    }

                    next.LastScrobble = current.LastScrobble;
                    next.LastScrobbleUrl = current.LastScrobbleUrl;
                    next.LastRewatch = current.LastRewatch;
                    next.ImportFromSimkl = current.ImportFromSimkl;
                    next.ImportInitialDone = current.ImportInitialDone;
                    next.ImportShowsStamp = current.ImportShowsStamp;
                    next.ImportMoviesStamp = current.ImportMoviesStamp;
                    next.ImportLastCheckUtc = current.ImportLastCheckUtc;
                    next.ImportLastReport = current.ImportLastReport;
                    next.ExportInitialDone = current.ExportInitialDone;
                    next.ExportLastReport = current.ExportLastReport;

                    var sameToken = string.Equals(current.UserToken, next.UserToken, StringComparison.Ordinal);
                    if (sameToken)
                    {
                        next.SimklUserName = current.SimklUserName;
                        next.SimklAccountId = current.SimklAccountId;
                        next.AccountType = current.AccountType;
                        next.AccountTypeCheckedUtc = current.AccountTypeCheckedUtc;
                        next.SettingsStamp = current.SettingsStamp;
                        next.LinkExpired = current.LinkExpired;
                    }
                    else
                    {
                        // A new token (or a log out): what was cached about the
                        // previous account must not survive it.
                        next.ForgetCachedAccount();
                        next.LinkExpired = false;
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