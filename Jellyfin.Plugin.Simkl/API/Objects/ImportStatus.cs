using System;

namespace Jellyfin.Plugin.Simkl.API.Objects
{
    /// <summary>
    /// The import state of a profile, for the settings pages.
    /// </summary>
    public class ImportStatus
    {
        /// <summary>
        /// Gets or sets a value indicating whether the import is enabled for the profile.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether items gone from Simkl are unmarked.
        /// </summary>
        public bool Unwatch { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the initial import was applied.
        /// </summary>
        public bool InitialDone { get; set; }

        /// <summary>
        /// Gets or sets when Simkl's activity was last checked.
        /// </summary>
        public DateTime? LastCheckUtc { get; set; }

        /// <summary>
        /// Gets or sets the last pass summary.
        /// </summary>
        public string? LastReport { get; set; }

        /// <summary>
        /// Gets or sets the number of changes waiting for the user's confirmation.
        /// </summary>
        public int PendingConfirmation { get; set; }

        /// <summary>
        /// Gets or sets the number of Simkl items still without a library match.
        /// </summary>
        public int UnmatchedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of items the import has marked so far.
        /// </summary>
        public int ImportedCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a pass can be undone.
        /// </summary>
        public bool CanUndo { get; set; }

        /// <summary>
        /// Gets or sets when the undoable pass ran.
        /// </summary>
        public DateTime? UndoRunUtc { get; set; }

        /// <summary>
        /// Gets or sets how many items the undoable pass changed.
        /// </summary>
        public int UndoCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the export to Simkl (step 2) was done.
        /// </summary>
        public bool ExportDone { get; set; }

        /// <summary>
        /// Gets or sets the last export summary.
        /// </summary>
        public string? ExportLastReport { get; set; }

        /// <summary>
        /// Gets or sets the number of items the export has sent to Simkl so far.
        /// </summary>
        public int ExportedCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the last export can be undone.
        /// </summary>
        public bool CanUndoExport { get; set; }

        /// <summary>
        /// Gets or sets how many items the undoable export sent.
        /// </summary>
        public int ExportUndoCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the continuous sync (step 3) is active:
        /// enabled, and both initial passes done.
        /// </summary>
        public bool SyncActive { get; set; }
    }
}
