using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Simkl.Services
{
    /// <summary>
    /// Brings the Simkl watch history into Jellyfin's played state, per user
    /// (experimental).
    /// </summary>
    /// <remarks>
    /// The flow protects the user: a preview shows what would change, the
    /// initial import only runs after that confirmation, later passes only
    /// read what changed on Simkl since the last one (activity feed first, as
    /// Simkl asks), stop at a cap and ask again beyond it, and every pass is
    /// journaled so it can be undone. Items are matched by provider ids only.
    /// Writes use <see cref="UserDataSaveReason.Import"/>, which the
    /// Jellyfin-to-Simkl sync ignores, so nothing loops back.
    /// </remarks>
    public class SimklImportService : IHostedService, IDisposable
    {
        /// <summary>
        /// Changes a continuous pass may apply on its own; beyond it the pass
        /// stops and the page asks for confirmation.
        /// </summary>
        public const int ContinuousCap = 200;

        private const int MaxUnmatched = 2000;
        private const int MaxReportLines = 200;
        private const string RefreshLibraryTaskKey = "RefreshLibrary";
        private const int ExportShowsPerRequest = 25;
        private const int ExportMoviesPerRequest = 100;

        private static readonly TimeSpan _minInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan _journalRetention = TimeSpan.FromDays(7);
        private static readonly TimeSpan _playbackDelay = TimeSpan.FromSeconds(20);
        private static readonly string[] _seriesIdKeys = { "Imdb", "Tvdb", "Tmdb" };
        private static readonly string[] _movieIdKeys = { "Imdb", "Tmdb" };
        private static readonly JsonSerializerOptions _stateJson = new JsonSerializerOptions { WriteIndented = true };

        private readonly SimklApi _simklApi;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ITaskManager _taskManager;
        private readonly ISessionManager _sessionManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly LibraryFilter _libraryFilter;
        private readonly ILogger<SimklImportService> _logger;
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new ConcurrentDictionary<Guid, SemaphoreSlim>();
        private readonly object _stateLock = new object();
        private ImportState? _state;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SimklImportService"/> class.
        /// </summary>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
        /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
        /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="libraryFilter">Instance of the <see cref="LibraryFilter"/>.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{SimklImportService}"/> interface.</param>
        public SimklImportService(
            SimklApi simklApi,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ITaskManager taskManager,
            ISessionManager sessionManager,
            IApplicationPaths applicationPaths,
            LibraryFilter libraryFilter,
            ILogger<SimklImportService> logger)
        {
            _simklApi = simklApi;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _taskManager = taskManager;
            _sessionManager = sessionManager;
            _applicationPaths = applicationPaths;
            _libraryFilter = libraryFilter;
            _logger = logger;
        }

        /// <summary>
        /// What a pass does with the changes it finds.
        /// </summary>
        public enum ImportMode
        {
            /// <summary>Full read, nothing applied.</summary>
            Preview,

            /// <summary>Full read, everything applied (the confirmed initial import).</summary>
            Initial,

            /// <summary>Changes since the last pass, applied up to the cap.</summary>
            Delta,
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _taskManager.TaskCompleted += OnTaskCompleted;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _taskManager.TaskCompleted -= OnTaskCompleted;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var gate in _userLocks.Values)
            {
                gate.Dispose();
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Reads the whole Simkl history and reports what an import would change, without touching anything.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public Task<ImportReport> PreviewAsync(Guid userId)
        {
            return RunAsync(userId, ImportMode.Preview, manual: true, applyAll: false);
        }

        /// <summary>
        /// Runs the initial import: the whole history, applied without a cap.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public Task<ImportReport> ApplyInitialAsync(Guid userId)
        {
            return RunAsync(userId, ImportMode.Initial, manual: true, applyAll: true);
        }

        /// <summary>
        /// Applies what changed on Simkl since the last pass.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <param name="manual">True when the user asked; skips the hourly spacing.</param>
        /// <param name="applyAll">True to apply beyond the cap (the user confirmed).</param>
        /// <returns>The report, or null when nothing was done.</returns>
        public Task<ImportReport> SyncAsync(Guid userId, bool manual, bool applyAll)
        {
            return RunAsync(userId, ImportMode.Delta, manual, applyAll);
        }

        /// <summary>
        /// Reverts the last pass that changed something for the user.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public ImportReport UndoLast(Guid userId)
        {
            var report = new ImportReport { Mode = "undo" };
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                report.Message = "Unknown user.";
                return report;
            }

            ImportRun? run;
            lock (_stateLock)
            {
                var state = LoadState().For(userId);
                run = state.Runs.Count > 0 ? state.Runs[state.Runs.Count - 1] : null;
            }

            if (run == null)
            {
                report.Message = "Nothing to undo.";
                return report;
            }

            var restored = 0;
            foreach (var change in run.Changes)
            {
                var item = _libraryManager.GetItemById(change.ItemId);
                if (item == null)
                {
                    continue;
                }

                var data = _userDataManager.GetUserData(user, item);
                if (data == null)
                {
                    continue;
                }

                data.Played = change.PreviousPlayed;
                data.PlayCount = change.PreviousPlayCount;
                data.LastPlayedDate = change.PreviousLastPlayed;
                data.PlaybackPositionTicks = change.PreviousPosition;
                _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                restored++;
            }

            lock (_stateLock)
            {
                var state = LoadState().For(userId);
                state.Runs.Remove(run);
                foreach (var change in run.Changes)
                {
                    if (change.NewPlayed)
                    {
                        state.Imported.Remove(change.ItemId);
                    }
                    else
                    {
                        state.Imported.Add(change.ItemId);
                    }
                }

                SaveState();
            }

            report.Applied = true;
            report.Message = string.Format(CultureInfo.InvariantCulture, "Reverted {0} item(s) from the pass of {1:yyyy-MM-dd HH:mm} UTC.", restored, run.Utc);
            RecordReport(userId, report.Message);
            _logger.LogInformation("Undid the Simkl import pass of {Utc} for {UserId}: {Count} item(s) restored", run.Utc, userId, restored);
            return report;
        }

        /// <summary>
        /// Gets the import status of a user, for the settings pages.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The status.</returns>
        public ImportStatus GetStatus(Guid userId)
        {
            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            if (config != null)
            {
                ResetIfAccountChanged(userId, config);
            }

            var status = new ImportStatus
            {
                Enabled = config?.ImportFromSimkl ?? false,
                Unwatch = config?.ImportUnwatch ?? false,
                InitialDone = config?.ImportInitialDone ?? false,
                LastCheckUtc = config?.ImportLastCheckUtc,
                LastReport = config?.ImportLastReport,
                ExportDone = config?.ExportInitialDone ?? false,
                ExportLastReport = config?.ExportLastReport,
                SyncActive = config != null && config.ImportFromSimkl && config.ImportInitialDone && config.ExportInitialDone,
            };

            lock (_stateLock)
            {
                var state = LoadState().Peek(userId);
                if (state == null)
                {
                    return status;
                }

                status.PendingConfirmation = state.PendingConfirmation;
                status.UnmatchedCount = state.Unmatched.Count;
                status.ImportedCount = state.Imported.Count;
                status.ExportedCount = state.Exported.Count;
                if (state.ExportRuns.Count > 0)
                {
                    var lastExport = state.ExportRuns[state.ExportRuns.Count - 1];
                    status.CanUndoExport = true;
                    status.ExportUndoCount = lastExport.Items.Count;
                }

                if (state.Runs.Count > 0)
                {
                    var last = state.Runs[state.Runs.Count - 1];
                    status.CanUndo = true;
                    status.UndoRunUtc = last.Utc;
                    status.UndoCount = last.Changes.Count;
                }
            }

            return status;
        }

        private static string ItemLine(string title, int? season, int? episode, DateTime? when)
        {
            var label = season != null && episode != null
                ? string.Format(CultureInfo.InvariantCulture, "{0} S{1:00}E{2:00}", title, season, episode)
                : title;
            return when == null
                ? label
                : string.Format(CultureInfo.InvariantCulture, "{0} ({1:yyyy-MM-dd})", label, when);
        }

        private static DateTime? ReadDate(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return date;
            }

            return null;
        }

        private static Dictionary<string, string> ReadIds(JsonElement media, string[] keys)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!media.TryGetProperty("ids", out var idsElement) || idsElement.ValueKind != JsonValueKind.Object)
            {
                return ids;
            }

            foreach (var key in keys)
            {
                if (idsElement.TryGetProperty(key.ToLowerInvariant(), out var value))
                {
                    var text = value.ValueKind == JsonValueKind.Number
                        ? value.GetRawText()
                        : value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        ids[key] = text!;
                    }
                }
            }

            return ids;
        }

        private static string ReadTitle(JsonElement media)
        {
            return media.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "?"
                : "?";
        }

        private async Task<ImportReport> RunAsync(Guid userId, ImportMode mode, bool manual, bool applyAll)
        {
            var report = new ImportReport { Mode = mode.ToString().ToLowerInvariant() };
            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            if (config == null || string.IsNullOrEmpty(config.UserToken))
            {
                report.Message = "This profile is not linked to Simkl.";
                return report;
            }

            await EnsureAccountAsync(userId, config).ConfigureAwait(false);
            if (mode == ImportMode.Delta && !config.ImportInitialDone)
            {
                report.Message = "Run step 1 (Simkl to Jellyfin) first.";
                return report;
            }

            if (mode == ImportMode.Delta && !manual && (!config.ImportFromSimkl || !config.ExportInitialDone))
            {
                report.Message = "Continuous sync is not active for this profile.";
                return report;
            }

            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                report.Message = "Unknown user.";
                return report;
            }

            var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(manual ? 30000 : 0).ConfigureAwait(false))
            {
                report.Message = "An import pass is already running for this profile.";
                return report;
            }

            try
            {
                if (mode == ImportMode.Delta && !manual)
                {
                    var pending = 0;
                    lock (_stateLock)
                    {
                        pending = LoadState().For(userId).PendingConfirmation;
                    }

                    if (pending > 0)
                    {
                        report.Message = "Waiting for confirmation on the settings page.";
                        report.NeedsConfirmation = true;
                        report.Remaining = pending;
                        return report;
                    }

                    if (config.ImportLastCheckUtc != null && DateTime.UtcNow - config.ImportLastCheckUtc.Value < _minInterval)
                    {
                        report.Message = "Checked less than an hour ago.";
                        return report;
                    }
                }

                var activity = await _simklApi.GetImportActivityAsync(config.UserToken, fresh: true).ConfigureAwait(false);
                if (activity.Unauthorized)
                {
                    report.Message = "Simkl rejected the saved login; link again.";
                    return report;
                }

                if (mode == ImportMode.Delta)
                {
                    config.ImportLastCheckUtc = DateTime.UtcNow;
                }

                var readShows = true;
                var readMovies = true;
                string? showsFrom = null;
                string? moviesFrom = null;
                if (mode == ImportMode.Delta)
                {
                    readShows = activity.ShowsStamp != null && !string.Equals(activity.ShowsStamp, config.ImportShowsStamp, StringComparison.Ordinal);
                    readMovies = activity.MoviesStamp != null && !string.Equals(activity.MoviesStamp, config.ImportMoviesStamp, StringComparison.Ordinal);
                    showsFrom = config.ImportShowsStamp;
                    moviesFrom = config.ImportMoviesStamp;
                    if (!readShows && !readMovies)
                    {
                        SimklPlugin.Instance?.SaveConfiguration();
                        report.Message = "Nothing changed on Simkl since the last pass.";
                        return report;
                    }
                }

                var plan = new ImportPlan();
                if (readShows)
                {
                    var body = await _simklApi.GetAllItemsRawAsync(config.UserToken, "shows", showsFrom).ConfigureAwait(false);
                    if (body == null)
                    {
                        report.Message = "Simkl did not return the show history.";
                        return report;
                    }

                    PlanShows(user, config, body, plan);
                }

                if (readMovies)
                {
                    var body = await _simklApi.GetAllItemsRawAsync(config.UserToken, "movies", moviesFrom).ConfigureAwait(false);
                    if (body == null)
                    {
                        report.Message = "Simkl did not return the movie history.";
                        return report;
                    }

                    PlanMovies(user, config, body, plan);
                }

                // Unwatch only makes sense against the full list: a delta can't
                // tell what disappeared.
                if (config.ImportUnwatch && mode != ImportMode.Delta)
                {
                    PlanUnwatch(user, userId, plan);
                }

                FillReport(report, plan);

                if (mode == ImportMode.Preview)
                {
                    report.Message = plan.Changes.Count == 0
                        ? "Nothing to change: Jellyfin already matches your Simkl history."
                        : string.Format(CultureInfo.InvariantCulture, "{0} change(s) would be applied.", plan.Changes.Count);
                    return report;
                }

                if (mode == ImportMode.Delta && !applyAll && plan.Changes.Count > ContinuousCap)
                {
                    lock (_stateLock)
                    {
                        LoadState().For(userId).PendingConfirmation = plan.Changes.Count;
                        SaveState();
                    }

                    report.NeedsConfirmation = true;
                    report.Remaining = plan.Changes.Count;
                    report.Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} changes found, more than the {1} a pass applies on its own. Confirm on the settings page to apply them.",
                        plan.Changes.Count,
                        ContinuousCap);
                    RecordReport(userId, report.Message);
                    SimklPlugin.Instance?.SaveConfiguration();
                    return report;
                }

                var run = Apply(user, plan, mode);
                lock (_stateLock)
                {
                    var state = LoadState().For(userId);
                    if (run.Changes.Count > 0)
                    {
                        state.Runs.Add(run);
                    }

                    foreach (var change in run.Changes)
                    {
                        if (change.NewPlayed)
                        {
                            state.Imported.Add(change.ItemId);
                        }
                        else
                        {
                            state.Imported.Remove(change.ItemId);
                        }
                    }

                    if (mode == ImportMode.Initial)
                    {
                        state.Unmatched.Clear();
                    }

                    var known = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var existing in state.Unmatched)
                    {
                        known.Add(existing.Key());
                    }

                    foreach (var unmatched in plan.Unmatched)
                    {
                        if (state.Unmatched.Count >= MaxUnmatched)
                        {
                            break;
                        }

                        if (known.Add(unmatched.Key()))
                        {
                            state.Unmatched.Add(unmatched);
                        }
                    }

                    state.PendingConfirmation = 0;
                    SaveState();
                }

                if (activity.ShowsStamp != null && (readShows || mode == ImportMode.Initial))
                {
                    config.ImportShowsStamp = activity.ShowsStamp;
                }

                if (activity.MoviesStamp != null && (readMovies || mode == ImportMode.Initial))
                {
                    config.ImportMoviesStamp = activity.MoviesStamp;
                }

                if (mode == ImportMode.Initial)
                {
                    config.ImportInitialDone = true;
                }

                report.Applied = true;
                report.RunUtc = run.Utc;
                report.Message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Applied {0} change(s): {1} episode(s) and {2} movie(s) marked played, {3} unmarked; {4} item(s) not in the library.",
                    run.Changes.Count,
                    report.MarkedEpisodes,
                    report.MarkedMovies,
                    report.Unmarked,
                    report.UnmatchedEpisodes + report.UnmatchedMovies);
                RecordReport(userId, report.Message);
                SimklPlugin.Instance?.SaveConfiguration();
                _logger.LogInformation("Simkl import ({Mode}) for {UserId}: {Message}", report.Mode, userId, report.Message);
                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simkl import ({Mode}) failed for {UserId}", report.Mode, userId);
                report.Message = "The import failed; see the server log for details.";
                return report;
            }
            finally
            {
                gate.Release();
            }
        }

        private void PlanShows(JellyfinUser user, UserConfig config, string body, ImportPlan plan)
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("shows", out var shows) || shows.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in shows.EnumerateArray())
            {
                if (!entry.TryGetProperty("show", out var show) || !entry.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var title = ReadTitle(show);
                var ids = ReadIds(show, _seriesIdKeys);
                var series = ids.Count == 0 ? null : FindItem(user, BaseItemKind.Series, ids);
                Dictionary<(int, int), BaseItem>? episodes = series == null ? null : IndexEpisodes(user, series);

                foreach (var season in seasons.EnumerateArray())
                {
                    if (!season.TryGetProperty("number", out var sn) || !sn.TryGetInt32(out var seasonNumber) || seasonNumber <= 0)
                    {
                        continue;
                    }

                    if (!season.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var ep in eps.EnumerateArray())
                    {
                        if (!ep.TryGetProperty("number", out var en) || !en.TryGetInt32(out var episodeNumber))
                        {
                            continue;
                        }

                        var watchedAt = ReadDate(ep, "watched_at") ?? ReadDate(entry, "last_watched_at");
                        if (episodes == null || !episodes.TryGetValue((seasonNumber, episodeNumber), out var item))
                        {
                            plan.UnmatchedEpisodes++;
                            if (series == null)
                            {
                                plan.UnmatchedShowTitles.Add(title);
                            }

                            plan.Unmatched.Add(new UnmatchedEntry
                            {
                                Type = "episode",
                                Title = title,
                                Ids = ids,
                                Season = seasonNumber,
                                Episode = episodeNumber,
                                WatchedAt = watchedAt,
                            });
                            continue;
                        }

                        plan.Present.Add(item.Id);
                        if (!config.ScrobbleShows || _libraryFilter.IsExcluded(config, item.Path))
                        {
                            plan.Excluded++;
                            continue;
                        }

                        if (_userDataManager.GetUserData(user, item)?.Played == true)
                        {
                            plan.AlreadyPlayed++;
                            continue;
                        }

                        plan.Changes.Add(new PlannedChange
                        {
                            Item = item,
                            Line = ItemLine(title, seasonNumber, episodeNumber, watchedAt),
                            WatchedAt = watchedAt,
                            Play = true,
                            IsMovie = false,
                        });
                    }
                }
            }
        }

        private void PlanMovies(JellyfinUser user, UserConfig config, string body, ImportPlan plan)
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in movies.EnumerateArray())
            {
                if (!entry.TryGetProperty("movie", out var movie))
                {
                    continue;
                }

                var status = entry.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = ReadTitle(movie);
                var ids = ReadIds(movie, _movieIdKeys);
                var watchedAt = ReadDate(entry, "last_watched_at");
                var item = ids.Count == 0 ? null : FindItem(user, BaseItemKind.Movie, ids);
                if (item == null)
                {
                    plan.UnmatchedMovies++;
                    plan.Unmatched.Add(new UnmatchedEntry { Type = "movie", Title = title, Ids = ids, WatchedAt = watchedAt });
                    continue;
                }

                plan.Present.Add(item.Id);
                if (!config.ScrobbleMovies || _libraryFilter.IsExcluded(config, item.Path))
                {
                    plan.Excluded++;
                    continue;
                }

                if (_userDataManager.GetUserData(user, item)?.Played == true)
                {
                    plan.AlreadyPlayed++;
                    continue;
                }

                plan.Changes.Add(new PlannedChange
                {
                    Item = item,
                    Line = ItemLine(title, null, null, watchedAt),
                    WatchedAt = watchedAt,
                    Play = true,
                    IsMovie = true,
                });
            }
        }

        private void PlanUnwatch(JellyfinUser user, Guid userId, ImportPlan plan)
        {
            List<Guid> imported;
            lock (_stateLock)
            {
                imported = new List<Guid>(LoadState().For(userId).Imported);
            }

            foreach (var itemId in imported)
            {
                if (plan.Present.Contains(itemId))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    continue;
                }

                if (_userDataManager.GetUserData(user, item)?.Played != true)
                {
                    continue;
                }

                plan.Changes.Add(new PlannedChange
                {
                    Item = item,
                    Line = item.Name + " (no longer watched on Simkl)",
                    Play = false,
                    IsMovie = item is MediaBrowser.Controller.Entities.Movies.Movie,
                });
            }
        }

        private ImportRun Apply(JellyfinUser user, ImportPlan plan, ImportMode mode)
        {
            var run = new ImportRun { Utc = DateTime.UtcNow, Mode = mode.ToString().ToLowerInvariant() };
            foreach (var change in plan.Changes)
            {
                var data = _userDataManager.GetUserData(user, change.Item);
                if (data == null)
                {
                    continue;
                }

                var record = new AppliedChange
                {
                    ItemId = change.Item.Id,
                    PreviousPlayed = data.Played,
                    PreviousPlayCount = data.PlayCount,
                    PreviousLastPlayed = data.LastPlayedDate,
                    PreviousPosition = data.PlaybackPositionTicks,
                    NewPlayed = change.Play,
                };

                if (change.Play)
                {
                    data.Played = true;
                    data.PlayCount++;
                    if (change.WatchedAt != null && (data.LastPlayedDate == null || change.WatchedAt > data.LastPlayedDate))
                    {
                        data.LastPlayedDate = change.WatchedAt;
                    }

                    data.PlaybackPositionTicks = 0;
                }
                else
                {
                    data.Played = false;
                    data.PlaybackPositionTicks = 0;
                }

                _userDataManager.SaveUserData(user, change.Item, data, UserDataSaveReason.Import, CancellationToken.None);
                run.Changes.Add(record);
            }

            return run;
        }

        /// <summary>
        /// Second chance for the entries no library item matched: after a
        /// library scan, new files may have arrived.
        /// </summary>
        private void RetryUnmatched(Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return;
            }

            List<UnmatchedEntry> entries;
            lock (_stateLock)
            {
                entries = new List<UnmatchedEntry>(LoadState().For(userId).Unmatched);
            }

            if (entries.Count == 0)
            {
                return;
            }

            var plan = new ImportPlan();
            var resolved = new List<UnmatchedEntry>();
            var episodeCache = new Dictionary<Guid, Dictionary<(int, int), BaseItem>>();
            foreach (var entry in entries)
            {
                BaseItem? item = null;
                if (entry.Type == "movie")
                {
                    item = FindItem(user, BaseItemKind.Movie, entry.Ids);
                }
                else if (entry.Season != null && entry.Episode != null)
                {
                    var series = FindItem(user, BaseItemKind.Series, entry.Ids);
                    if (series != null)
                    {
                        if (!episodeCache.TryGetValue(series.Id, out var episodes))
                        {
                            episodes = IndexEpisodes(user, series);
                            episodeCache[series.Id] = episodes;
                        }

                        episodes.TryGetValue((entry.Season.Value, entry.Episode.Value), out item);
                    }
                }

                if (item == null)
                {
                    continue;
                }

                resolved.Add(entry);
                if (_userDataManager.GetUserData(user, item)?.Played == true)
                {
                    continue;
                }

                plan.Changes.Add(new PlannedChange
                {
                    Item = item,
                    Line = ItemLine(entry.Title, entry.Season, entry.Episode, entry.WatchedAt),
                    WatchedAt = entry.WatchedAt,
                    Play = true,
                    IsMovie = entry.Type == "movie",
                });
            }

            if (resolved.Count == 0)
            {
                return;
            }

            var run = Apply(user, plan, ImportMode.Delta);
            lock (_stateLock)
            {
                var state = LoadState().For(userId);
                foreach (var entry in resolved)
                {
                    state.Unmatched.Remove(entry);
                }

                if (run.Changes.Count > 0)
                {
                    state.Runs.Add(run);
                    foreach (var change in run.Changes)
                    {
                        state.Imported.Add(change.ItemId);
                    }
                }

                SaveState();
            }

            if (run.Changes.Count > 0)
            {
                var message = string.Format(CultureInfo.InvariantCulture, "After the library scan, {0} item(s) from your Simkl history were marked played.", run.Changes.Count);
                RecordReport(userId, message);
                SimklPlugin.Instance?.SaveConfiguration();
                _logger.LogInformation("Simkl import for {UserId}: {Message}", userId, message);
            }
        }

        /// <summary>
        /// Lists what an export of Jellyfin's played history to Simkl would send, without sending.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public Task<ImportReport> ExportPreviewAsync(Guid userId)
        {
            return ExportAsync(userId, apply: false);
        }

        /// <summary>
        /// Sends Jellyfin's played history to the user's Simkl history (step 2 of the sync setup).
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public Task<ImportReport> ExportApplyAsync(Guid userId)
        {
            return ExportAsync(userId, apply: true);
        }

        /// <summary>
        /// Removes from Simkl what the last export sent.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>The report.</returns>
        public async Task<ImportReport> ExportUndoAsync(Guid userId)
        {
            var report = new ImportReport { Mode = "export-undo" };
            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            var user = _userManager.GetUserById(userId);
            if (config == null || string.IsNullOrEmpty(config.UserToken) || user == null)
            {
                report.Message = "This profile is not linked to Simkl.";
                return report;
            }

            await EnsureAccountAsync(userId, config).ConfigureAwait(false);

            ExportRun? run;
            lock (_stateLock)
            {
                var state = LoadState().For(userId);
                run = state.ExportRuns.Count > 0 ? state.ExportRuns[state.ExportRuns.Count - 1] : null;
            }

            if (run == null)
            {
                report.Message = "Nothing to undo.";
                return report;
            }

            var items = new List<BaseItem>();
            foreach (var itemId in run.Items)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item != null)
                {
                    items.Add(item);
                }
            }

            var plan = BuildExportPlan(user, config, items, skipImported: false);
            var removed = 0;
            foreach (var history in plan.Batches)
            {
                var response = await _simklApi.RemoveFromHistory(history, config.UserToken).ConfigureAwait(false);
                if (response == null)
                {
                    report.Message = "Simkl did not accept the removal; nothing was changed locally. Try again later.";
                    return report;
                }

                removed += CountItems(history);
            }

            lock (_stateLock)
            {
                var state = LoadState().For(userId);
                state.ExportRuns.Remove(run);
                foreach (var itemId in run.Items)
                {
                    state.Exported.Remove(itemId);
                }

                SaveState();
            }

            report.Applied = true;
            report.Message = string.Format(CultureInfo.InvariantCulture, "Removed {0} item(s) from your Simkl history (export of {1:yyyy-MM-dd HH:mm} UTC).", removed, run.Utc);
            config.ExportLastReport = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} UTC: {1}", DateTime.UtcNow, report.Message);
            SimklPlugin.Instance?.SaveConfiguration();
            _logger.LogInformation("Undid the Simkl export of {Utc} for {UserId}: {Count} item(s) removed", run.Utc, userId, removed);
            return report;
        }

        private static int CountItems(SimklHistory history)
        {
            var count = history.Movies.Count;
            foreach (var show in history.Shows)
            {
                foreach (var season in show.Seasons ?? Array.Empty<Season>())
                {
                    count += season.Episodes.Count;
                }
            }

            return count;
        }

        private async Task<ImportReport> ExportAsync(Guid userId, bool apply)
        {
            var report = new ImportReport { Mode = apply ? "export" : "export-preview" };
            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            var user = _userManager.GetUserById(userId);
            if (config == null || string.IsNullOrEmpty(config.UserToken) || user == null)
            {
                report.Message = "This profile is not linked to Simkl.";
                return report;
            }

            await EnsureAccountAsync(userId, config).ConfigureAwait(false);

            var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(30000).ConfigureAwait(false))
            {
                report.Message = "A pass is already running for this profile.";
                return report;
            }

            try
            {
                var played = new List<BaseItem>();
                foreach (var kind in new[] { BaseItemKind.Episode, BaseItemKind.Movie })
                {
                    var query = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[] { kind },
                        IsPlayed = true,
                        Recursive = true,
                        IsVirtualItem = false,
                    };
                    played.AddRange(_libraryManager.GetItemList(query));
                }

                var plan = BuildExportPlan(user, config, played, skipImported: true);
                report.MarkedEpisodes = plan.Episodes;
                report.MarkedMovies = plan.Movies;
                report.TotalChanges = plan.Episodes + plan.Movies;
                report.UnmatchedEpisodes = plan.NoIdsEpisodes;
                report.UnmatchedMovies = plan.NoIdsMovies;
                report.AlreadyPlayed = plan.SkippedImported;
                foreach (var line in plan.Lines)
                {
                    if (report.Lines.Count >= MaxReportLines)
                    {
                        break;
                    }

                    report.Lines.Add("> " + line);
                }

                if (!apply)
                {
                    report.Message = report.TotalChanges == 0
                        ? "Nothing to send: Jellyfin has no played item that Simkl would need."
                        : string.Format(CultureInfo.InvariantCulture, "{0} item(s) would be sent to your Simkl history.", report.TotalChanges);
                    return report;
                }

                var sent = 0;
                var complete = true;
                foreach (var history in plan.Batches)
                {
                    var response = await _simklApi.AddToHistory(history, config.UserToken).ConfigureAwait(false);
                    if (response == null)
                    {
                        complete = false;
                        report.Message = string.Format(CultureInfo.InvariantCulture, "Simkl stopped accepting the history after {0} item(s); the rest was not sent. Run the export again later.", sent);
                        break;
                    }

                    sent += CountItems(history);
                }

                if (sent > 0)
                {
                    lock (_stateLock)
                    {
                        var state = LoadState().For(userId);
                        var run = new ExportRun { Utc = DateTime.UtcNow };
                        foreach (var itemId in plan.ItemIds)
                        {
                            run.Items.Add(itemId);
                            state.Exported.Add(itemId);
                        }

                        state.ExportRuns.Add(run);
                        SaveState();
                    }
                }

                if (string.IsNullOrEmpty(report.Message))
                {
                    report.Message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Sent {0} item(s) to your Simkl history: {1} episode(s) and {2} movie(s); {3} item(s) had no usable ids.",
                        sent,
                        plan.Episodes,
                        plan.Movies,
                        plan.NoIdsEpisodes + plan.NoIdsMovies);
                }

                report.Applied = sent > 0 || complete;
                if (complete)
                {
                    config.ExportInitialDone = true;
                }

                config.ExportLastReport = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} UTC: {1}", DateTime.UtcNow, report.Message);
                SimklPlugin.Instance?.SaveConfiguration();
                _logger.LogInformation("Simkl export for {UserId}: {Message}", userId, report.Message);
                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simkl export failed for {UserId}", userId);
                report.Message = "The export failed; see the server log for details.";
                return report;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Groups played items into Simkl history requests, with their real watch
        /// dates. Items the Simkl-to-Jellyfin import marked are left out (they
        /// came from Simkl), as are excluded libraries and items without ids.
        /// </summary>
        private ExportPlan BuildExportPlan(JellyfinUser user, UserConfig config, IReadOnlyList<BaseItem> items, bool skipImported)
        {
            var plan = new ExportPlan();
            HashSet<Guid> imported;
            HashSet<Guid> exported;
            lock (_stateLock)
            {
                var state = LoadState().For(user.Id);
                imported = new HashSet<Guid>(state.Imported);
                exported = new HashSet<Guid>(state.Exported);
            }

            var shows = new Dictionary<Guid, (BaseItem Series, Dictionary<string, string> Ids, SortedDictionary<int, SortedDictionary<int, DateTime?>> Seasons)>();
            var movies = new List<SimklMovie>();
            foreach (var item in items)
            {
                // Items that came from Simkl (step 1) or were already sent by a
                // previous export are on Simkl already.
                if (skipImported && (imported.Contains(item.Id) || exported.Contains(item.Id)))
                {
                    plan.SkippedImported++;
                    continue;
                }

                var isMovieItem = item is MediaBrowser.Controller.Entities.Movies.Movie;
                if ((isMovieItem ? !config.ScrobbleMovies : !config.ScrobbleShows) || _libraryFilter.IsExcluded(config, item.Path))
                {
                    continue;
                }

                var watchedAt = AsUtc(_userDataManager.GetUserData(user, item)?.LastPlayedDate);
                if (item is MediaBrowser.Controller.Entities.Movies.Movie)
                {
                    var ids = PickIds(item.ProviderIds, _movieIdKeys);
                    if (ids.Count == 0)
                    {
                        plan.NoIdsMovies++;
                        continue;
                    }

                    movies.Add(new SimklMovie
                    {
                        Title = item.Name,
                        Year = item.ProductionYear,
                        Ids = new SimklMovieIds(ids),
                        WatchedAt = watchedAt ?? DateTime.UtcNow,
                    });
                    plan.ItemIds.Add(item.Id);
                    plan.Movies++;
                    plan.Lines.Add(ItemLine(item.Name, null, null, watchedAt));
                    continue;
                }

                if (item is not MediaBrowser.Controller.Entities.TV.Episode episode
                    || episode.ParentIndexNumber is not int season
                    || episode.IndexNumber is not int number
                    || season <= 0)
                {
                    continue;
                }

                var seriesId = episode.SeriesId;
                if (!shows.TryGetValue(seriesId, out var entry))
                {
                    var series = _libraryManager.GetItemById(seriesId);
                    if (series == null)
                    {
                        plan.NoIdsEpisodes++;
                        continue;
                    }

                    var seriesIds = PickIds(series.ProviderIds, _seriesIdKeys);
                    entry = (series, seriesIds, new SortedDictionary<int, SortedDictionary<int, DateTime?>>());
                    shows[seriesId] = entry;
                }

                if (entry.Ids.Count == 0)
                {
                    plan.NoIdsEpisodes++;
                    continue;
                }

                if (!entry.Seasons.TryGetValue(season, out var episodes))
                {
                    episodes = new SortedDictionary<int, DateTime?>();
                    entry.Seasons[season] = episodes;
                }

                episodes[number] = watchedAt;
                plan.ItemIds.Add(item.Id);
                plan.Episodes++;
                plan.Lines.Add(ItemLine(entry.Series.Name, season, number, watchedAt));
            }

            var current = new SimklHistory();
            var showsInBatch = 0;
            foreach (var entry in shows.Values)
            {
                if (entry.Ids.Count == 0 || entry.Seasons.Count == 0)
                {
                    continue;
                }

                var seasons = new List<Season>();
                foreach (var season in entry.Seasons)
                {
                    var eps = new List<ShowEpisode>();
                    foreach (var ep in season.Value)
                    {
                        eps.Add(new ShowEpisode { Number = ep.Key, WatchedAt = ep.Value ?? DateTime.UtcNow });
                    }

                    seasons.Add(new Season { Number = season.Key, Episodes = eps });
                }

                current.Shows.Add(new SimklShow
                {
                    Title = entry.Series.Name,
                    Year = entry.Series.ProductionYear,
                    Ids = new SimklShowIds(new Dictionary<string, string>(entry.Ids, StringComparer.OrdinalIgnoreCase)),
                    Seasons = seasons,
                });

                if (++showsInBatch >= ExportShowsPerRequest)
                {
                    plan.Batches.Add(current);
                    current = new SimklHistory();
                    showsInBatch = 0;
                }
            }

            foreach (var movie in movies)
            {
                current.Movies.Add(movie);
                if (current.Movies.Count >= ExportMoviesPerRequest)
                {
                    plan.Batches.Add(current);
                    current = new SimklHistory();
                    showsInBatch = 0;
                }
            }

            if (current.Shows.Count > 0 || current.Movies.Count > 0)
            {
                plan.Batches.Add(current);
            }

            return plan;
        }

        private static DateTime? AsUtc(DateTime? value)
        {
            if (value == null)
            {
                return null;
            }

            return value.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : value.Value.ToUniversalTime();
        }

        private static Dictionary<string, string> PickIds(Dictionary<string, string>? providerIds, string[] keys)
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (providerIds == null)
            {
                return ids;
            }

            foreach (var key in keys)
            {
                foreach (var pair in providerIds)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        ids[key] = pair.Value;
                        break;
                    }
                }
            }

            return ids;
        }

        private BaseItem? FindItem(JellyfinUser user, BaseItemKind kind, Dictionary<string, string> ids)
        {
            if (ids.Count == 0)
            {
                return null;
            }

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { kind },
                HasAnyProviderId = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase),
                Recursive = true,
                Limit = 5,
            };

            foreach (var candidate in _libraryManager.GetItemList(query))
            {
                if (candidate.IsVisible(user, true))
                {
                    return candidate;
                }
            }

            return null;
        }

        private Dictionary<(int, int), BaseItem> IndexEpisodes(JellyfinUser user, BaseItem series)
        {
            var index = new Dictionary<(int, int), BaseItem>();
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                Recursive = true,
                IsVirtualItem = false,
            };

            foreach (var episode in _libraryManager.GetItemList(query))
            {
                if (episode.ParentIndexNumber is int season && episode.IndexNumber is int number && season > 0)
                {
                    index.TryAdd((season, number), episode);
                }
            }

            return index;
        }

        private void FillReport(ImportReport report, ImportPlan plan)
        {
            foreach (var change in plan.Changes)
            {
                if (!change.Play)
                {
                    report.Unmarked++;
                }
                else if (change.IsMovie)
                {
                    report.MarkedMovies++;
                }
                else
                {
                    report.MarkedEpisodes++;
                }

                if (report.Lines.Count < MaxReportLines)
                {
                    report.Lines.Add((change.Play ? "+ " : "- ") + change.Line);
                }
            }

            report.AlreadyPlayed = plan.AlreadyPlayed;
            report.Excluded = plan.Excluded;
            report.UnmatchedEpisodes = plan.UnmatchedEpisodes;
            report.UnmatchedMovies = plan.UnmatchedMovies;
            report.UnmatchedShows = plan.UnmatchedShowTitles.Count;
            report.TotalChanges = plan.Changes.Count;
        }

        private void RecordReport(Guid userId, string message)
        {
            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            if (config != null)
            {
                config.ImportLastReport = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} UTC: {1}", DateTime.UtcNow, message);
            }
        }

        private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            var userId = e.Session?.UserId;
            if (userId == null || userId == Guid.Empty)
            {
                return;
            }

            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId.Value);
            if (config == null || !config.ImportFromSimkl || !config.ImportInitialDone || !config.ExportInitialDone || string.IsNullOrEmpty(config.UserToken))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Let the stop scrobble land first: it changes Simkl's activity.
                    await Task.Delay(_playbackDelay).ConfigureAwait(false);
                    await SyncAsync(userId.Value, manual: false, applyAll: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Simkl import after playback failed");
                }
            });
        }

        private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
        {
            if (!string.Equals(e.Task?.ScheduledTask?.Key, RefreshLibraryTaskKey, StringComparison.Ordinal))
            {
                return;
            }

            var configs = SimklPlugin.Instance?.Configuration.UserConfigs;
            if (configs == null)
            {
                return;
            }

            var users = new List<Guid>();
            foreach (var config in configs)
            {
                if (config.ImportFromSimkl && config.ImportInitialDone && config.ExportInitialDone && !string.IsNullOrEmpty(config.UserToken))
                {
                    users.Add(config.Id);
                }
            }

            if (users.Count == 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                foreach (var userId in users)
                {
                    try
                    {
                        RetryUnmatched(userId);
                        await SyncAsync(userId, manual: false, applyAll: false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Simkl import after library scan failed for {UserId}", userId);
                    }
                }
            });
        }

        private string StatePath()
        {
            return Path.Combine(_applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Simkl.import.json");
        }

        /// <summary>
        /// Makes sure the Simkl account id is known for the profile, then forgets
        /// the sync state if the profile is now linked to another account.
        /// </summary>
        private async Task EnsureAccountAsync(Guid userId, UserConfig config)
        {
            if (config.SimklAccountId == null && !string.IsNullOrEmpty(config.UserToken))
            {
                try
                {
                    await _simklApi.GetUserSettings(config.UserToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read the Simkl account of {UserId} before a sync pass", userId);
                }
            }

            ResetIfAccountChanged(userId, config);
        }

        /// <summary>
        /// The sync state (what was imported, exported, the delta stamps and the
        /// step flags) belongs to one Simkl account. When the profile is linked
        /// to another account, it starts over from step 1; what was marked in
        /// Jellyfin stays as it is.
        /// </summary>
        private void ResetIfAccountChanged(Guid userId, UserConfig config)
        {
            var accountId = config.SimklAccountId;
            if (accountId == null)
            {
                return;
            }

            lock (_stateLock)
            {
                var all = LoadState();
                var state = all.For(userId);
                if (state.AccountId == null)
                {
                    // First time the account is seen: adopt it as the anchor.
                    state.AccountId = accountId;
                    SaveState();
                    return;
                }

                if (state.AccountId == accountId)
                {
                    return;
                }

                all.Users[userId.ToString("N")] = new UserImportState { AccountId = accountId };
                SaveState();
            }

            config.ImportInitialDone = false;
            config.ExportInitialDone = false;
            config.ImportFromSimkl = false;
            config.ImportShowsStamp = null;
            config.ImportMoviesStamp = null;
            config.ImportLastCheckUtc = null;
            config.ExportLastReport = null;
            config.ImportLastReport = "This profile is now linked to another Simkl account: the sync starts over from step 1.";
            SimklPlugin.Instance?.SaveConfiguration();
            _logger.LogWarning("Profile {UserId} is linked to another Simkl account; its Simkl sync state was reset", userId);
        }

        private ImportState LoadState()
        {
            if (_state != null)
            {
                return _state;
            }

            try
            {
                var path = StatePath();
                if (File.Exists(path))
                {
                    _state = JsonSerializer.Deserialize<ImportState>(File.ReadAllText(path)) ?? new ImportState();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the Simkl import state; starting empty");
            }

            _state ??= new ImportState();
            return _state;
        }

        private void SaveState()
        {
            if (_state == null)
            {
                return;
            }

            var cutoff = DateTime.UtcNow - _journalRetention;
            foreach (var userState in _state.Users.Values)
            {
                userState.Runs.RemoveAll(r => r.Utc < cutoff);
                userState.ExportRuns.RemoveAll(r => r.Utc < cutoff);
            }

            try
            {
                File.WriteAllText(StatePath(), JsonSerializer.Serialize(_state, _stateJson));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist the Simkl import state");
            }
        }

        /// <summary>
        /// What a pass found and would change.
        /// </summary>
        private sealed class ImportPlan
        {
            public List<PlannedChange> Changes { get; } = new List<PlannedChange>();

            public HashSet<Guid> Present { get; } = new HashSet<Guid>();

            public List<UnmatchedEntry> Unmatched { get; } = new List<UnmatchedEntry>();

            public HashSet<string> UnmatchedShowTitles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int UnmatchedEpisodes { get; set; }

            public int UnmatchedMovies { get; set; }

            public int AlreadyPlayed { get; set; }

            public int Excluded { get; set; }
        }

        /// <summary>
        /// What an export would send to Simkl.
        /// </summary>
        private sealed class ExportPlan
        {
            public List<SimklHistory> Batches { get; } = new List<SimklHistory>();

            public List<Guid> ItemIds { get; } = new List<Guid>();

            public List<string> Lines { get; } = new List<string>();

            public int Episodes { get; set; }

            public int Movies { get; set; }

            public int NoIdsEpisodes { get; set; }

            public int NoIdsMovies { get; set; }

            public int SkippedImported { get; set; }
        }

        private sealed class PlannedChange
        {
            public BaseItem Item { get; set; } = null!;

            public string Line { get; set; } = string.Empty;

            public DateTime? WatchedAt { get; set; }

            public bool Play { get; set; }

            public bool IsMovie { get; set; }
        }

        /// <summary>
        /// Persisted state: journal, imported items and unmatched entries per user.
        /// </summary>
        private sealed class ImportState
        {
            [JsonPropertyName("users")]
            public Dictionary<string, UserImportState> Users { get; set; } = new Dictionary<string, UserImportState>(StringComparer.OrdinalIgnoreCase);

            public UserImportState? Peek(Guid userId)
            {
                return Users.TryGetValue(userId.ToString("N"), out var state) ? state : null;
            }

            public UserImportState For(Guid userId)
            {
                var key = userId.ToString("N");
                if (!Users.TryGetValue(key, out var state))
                {
                    state = new UserImportState();
                    Users[key] = state;
                }

                return state;
            }
        }

        private sealed class UserImportState
        {
            [JsonPropertyName("accountId")]
            public int? AccountId { get; set; }

            [JsonPropertyName("imported")]
            public HashSet<Guid> Imported { get; set; } = new HashSet<Guid>();

            [JsonPropertyName("unmatched")]
            public List<UnmatchedEntry> Unmatched { get; set; } = new List<UnmatchedEntry>();

            [JsonPropertyName("runs")]
            public List<ImportRun> Runs { get; set; } = new List<ImportRun>();

            [JsonPropertyName("pendingConfirmation")]
            public int PendingConfirmation { get; set; }

            [JsonPropertyName("exported")]
            public HashSet<Guid> Exported { get; set; } = new HashSet<Guid>();

            [JsonPropertyName("exportRuns")]
            public List<ExportRun> ExportRuns { get; set; } = new List<ExportRun>();
        }

        private sealed class ExportRun
        {
            [JsonPropertyName("utc")]
            public DateTime Utc { get; set; }

            [JsonPropertyName("items")]
            public List<Guid> Items { get; set; } = new List<Guid>();
        }

        private sealed class UnmatchedEntry
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "episode";

            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            [JsonPropertyName("ids")]
            public Dictionary<string, string> Ids { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            [JsonPropertyName("season")]
            public int? Season { get; set; }

            [JsonPropertyName("episode")]
            public int? Episode { get; set; }

            [JsonPropertyName("watchedAt")]
            public DateTime? WatchedAt { get; set; }

            public string Key()
            {
                var ids = new List<string>();
                foreach (var pair in Ids)
                {
                    ids.Add(pair.Key.ToLowerInvariant() + "=" + pair.Value);
                }

                ids.Sort(StringComparer.Ordinal);
                return Type + "|" + string.Join(",", ids) + "|" + Season + "|" + Episode;
            }
        }

        private sealed class ImportRun
        {
            [JsonPropertyName("utc")]
            public DateTime Utc { get; set; }

            [JsonPropertyName("mode")]
            public string Mode { get; set; } = string.Empty;

            [JsonPropertyName("changes")]
            public List<AppliedChange> Changes { get; set; } = new List<AppliedChange>();
        }

        private sealed class AppliedChange
        {
            [JsonPropertyName("item")]
            public Guid ItemId { get; set; }

            [JsonPropertyName("prevPlayed")]
            public bool PreviousPlayed { get; set; }

            [JsonPropertyName("prevCount")]
            public int PreviousPlayCount { get; set; }

            [JsonPropertyName("prevLast")]
            public DateTime? PreviousLastPlayed { get; set; }

            [JsonPropertyName("prevPos")]
            public long PreviousPosition { get; set; }

            [JsonPropertyName("newPlayed")]
            public bool NewPlayed { get; set; }
        }
    }
}
