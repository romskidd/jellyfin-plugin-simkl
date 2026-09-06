using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Simkl.API.Objects
{
    /// <summary>
    /// Show episode.
    /// </summary>
    public class ShowEpisode
    {
        /// <summary>
        /// Gets or sets episode number.
        /// </summary>
        [JsonPropertyName("number")]
        public int? Number { get; set; }

        /// <summary>
        /// Gets or sets when the episode was watched, for history writes.
        /// </summary>
        [JsonPropertyName("watched_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? WatchedAt { get; set; }
        // TODO: watched_at
    }
}