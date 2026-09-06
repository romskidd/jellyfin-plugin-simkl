using System;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Simkl.Configuration
{
    /// <summary>
    /// Class needed to create a Plugin and configure it.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            UserConfigs = Array.Empty<UserConfig>();
        }

        /// <summary>
        /// Gets or sets the list of user configs.
        /// </summary>
        public UserConfig[] UserConfigs { get; set; }

        /// <summary>
        /// Get config by id.
        /// </summary>
        /// <param name="id">The user id.</param>
        /// <returns>Stored user config.</returns>
        public UserConfig? GetByGuid(Guid id)
        {
            return UserConfigs.FirstOrDefault(c => c.Id == id);
        }

        /// <summary>
        /// Gets the config for a user, creating an empty one when the user has
        /// never been configured yet.
        /// </summary>
        /// <param name="id">The user id.</param>
        /// <returns>The stored or newly created user config.</returns>
        public UserConfig GetOrCreate(Guid id)
        {
            var existing = GetByGuid(id);
            if (existing != null)
            {
                return existing;
            }

            var created = new UserConfig { Id = id };
            UserConfigs = UserConfigs.Append(created).ToArray();
            return created;
        }

        /// <summary>
        /// Delete user token.
        /// </summary>
        /// <param name="userToken">User token.</param>
        public void DeleteUserToken(string userToken)
        {
            if (string.IsNullOrEmpty(userToken))
            {
                // An unlinked profile has an empty token: matching it would flag
                // every never-linked profile as "link expired".
                return;
            }

            foreach (var config in UserConfigs)
            {
                if (config.UserToken == userToken)
                {
                    config.UserToken = string.Empty;

                    // Remember why the token went away, so the pages can tell the
                    // user to link again instead of silently looking logged out.
                    config.LinkExpired = true;
                }
            }

            SimklPlugin.Instance?.SaveConfiguration();
        }
    }
}