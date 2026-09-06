using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Simkl.API.Objects
{
    /// <summary>
    /// The outcome of a Simkl import pass: what was (or would be) changed.
    /// </summary>
    public class ImportReport
    {
        /// <summary>
        /// Gets or sets the pass kind: preview, initial, delta or undo.
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether changes were written to Jellyfin.
        /// </summary>
        public bool Applied { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the pass stopped at the cap and waits for the user.
        /// </summary>
        public bool NeedsConfirmation { get; set; }

        /// <summary>
        /// Gets or sets the number of changes waiting for confirmation.
        /// </summary>
        public int Remaining { get; set; }

        /// <summary>
        /// Gets or sets the total number of changes found.
        /// </summary>
        public int TotalChanges { get; set; }

        /// <summary>
        /// Gets or sets the number of episodes marked (or to mark) played.
        /// </summary>
        public int MarkedEpisodes { get; set; }

        /// <summary>
        /// Gets or sets the number of movies marked (or to mark) played.
        /// </summary>
        public int MarkedMovies { get; set; }

        /// <summary>
        /// Gets or sets the number of items unmarked (or to unmark).
        /// </summary>
        public int Unmarked { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl items Jellyfin already had as played.
        /// </summary>
        public int AlreadyPlayed { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl shows with no series in the library.
        /// </summary>
        public int UnmatchedShows { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl episodes with no file in the library.
        /// </summary>
        public int UnmatchedEpisodes { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl movies with no file in the library.
        /// </summary>
        public int UnmatchedMovies { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl items skipped because they sit in a library the user excluded.
        /// </summary>
        public int Excluded { get; set; }

        /// <summary>
        /// Gets or sets when the pass was applied, when it was.
        /// </summary>
        public DateTime? RunUtc { get; set; }

        /// <summary>
        /// Gets or sets a one-line summary for the page.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets the first changes, one line each, prefixed with + (mark) or - (unmark).
        /// </summary>
        public List<string> Lines { get; } = new List<string>();
    }
}
