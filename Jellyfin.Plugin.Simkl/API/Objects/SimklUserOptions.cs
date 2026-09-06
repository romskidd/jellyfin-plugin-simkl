using System.Collections.Generic;

namespace Jellyfin.Plugin.Simkl.API.Objects
{
    /// <summary>
    /// The scrobbling options a Jellyfin user is allowed to change for their
    /// own profile from the self-service page.
    /// </summary>
    /// <remarks>
    /// Deliberately a subset of <see cref="Configuration.UserConfig"/>: the
    /// user id and the Simkl token are never accepted from the request body.
    /// </remarks>
    public class SimklUserOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether real-time scrobbling is enabled.
        /// </summary>
        public bool EnablePlaybackScrobbling { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether movies are scrobbled.
        /// </summary>
        public bool ScrobbleMovies { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether episodes are scrobbled.
        /// </summary>
        public bool ScrobbleShows { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether manual "mark played" actions
        /// are pushed to the Simkl history.
        /// </summary>
        public bool SyncMarkPlayed { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unmarking an item removes it
        /// from the Simkl history.
        /// </summary>
        public bool SyncMarkUnplayed { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether finishing something already
        /// watched is recorded as a Simkl rewatch session (Pro/VIP only).
        /// </summary>
        public bool EnableRewatches { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Simkl history is imported into Jellyfin (experimental).
        /// </summary>
        public bool ImportFromSimkl { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether items gone from the Simkl history are unmarked.
        /// </summary>
        public bool ImportUnwatch { get; set; }

        /// <summary>
        /// Gets or sets the minimum runtime, in minutes, below which nothing is scrobbled.
        /// </summary>
        public int MinLength { get; set; }

        /// <summary>
        /// Gets or sets the ids of the libraries that are never scrobbled.
        /// </summary>
        public IReadOnlyList<string>? ExcludedLibraries { get; set; }
    }
}
