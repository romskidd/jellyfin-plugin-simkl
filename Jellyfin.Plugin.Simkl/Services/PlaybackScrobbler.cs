using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Exceptions;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Simkl.Services
{
    /// <summary>
    /// Real-time playback scrobbler.
    /// </summary>
    /// <remarks>
    /// Implements the Simkl scrobble lifecycle (<c>start</c> / <c>pause</c> / <c>stop</c>).
    /// Events are sent only on real player transitions — playback start, pause,
    /// resume and stop — never on a timer. Simkl extrapolates progress between
    /// events from the item runtime, so polling would only waste quota and trip
    /// the 20-second per-user lock. The watched mark is decided server-side: a
    /// <c>stop</c> with progress &gt;= 80 is recorded as watched, anything lower is
    /// kept as a resumable playback.
    /// </remarks>
    public class PlaybackScrobbler : IHostedService
    {
        // A stop past this much is a finished watch worth retrying if Simkl
        // didn't confirm it; below, losing the event costs nothing.
        private const double RetryThreshold = 80d;

        // How much of the runtime must actually go by during one session before
        // it counts as a rewatch. Resuming near the end, or skipping ahead to
        // check something, must not file a viewing that didn't happen.
        private const double MinRewatchSpan = 50d;

        // Skip re-sending the same action for the same item within this window
        // to stay clear of the server's 20-second per-user lock.
        private static readonly TimeSpan _minActionInterval = TimeSpan.FromSeconds(20);

        private readonly ISessionManager _sessionManager;
        private readonly ILogger<PlaybackScrobbler> _logger;
        private readonly SimklApi _simklApi;
        private readonly ILibraryManager _libraryManager;
        private readonly LibraryFilter _libraryFilter;
        private readonly ScrobbleRetryQueue _retryQueue;
        private readonly ConcurrentDictionary<string, SessionState> _sessions;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackScrobbler"/> class.
        /// </summary>
        /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{PlaybackScrobbler}"/> interface.</param>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="libraryFilter">Instance of the <see cref="LibraryFilter"/>.</param>
        /// <param name="retryQueue">Instance of the <see cref="ScrobbleRetryQueue"/>.</param>
        public PlaybackScrobbler(
            ISessionManager sessionManager,
            ILogger<PlaybackScrobbler> logger,
            SimklApi simklApi,
            ILibraryManager libraryManager,
            LibraryFilter libraryFilter,
            ScrobbleRetryQueue retryQueue)
        {
            _sessionManager = sessionManager;
            _logger = logger;
            _simklApi = simklApi;
            _libraryManager = libraryManager;
            _libraryFilter = libraryFilter;
            _retryQueue = retryQueue;
            _sessions = new ConcurrentDictionary<string, SessionState>();
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            return Task.CompletedTask;
        }

        private static bool TypeEnabled(UserConfig config, BaseItemKind type)
        {
            return type switch
            {
                BaseItemKind.Movie => config.ScrobbleMovies,
                BaseItemKind.Episode => config.ScrobbleShows,
                BaseItemKind.Series => config.ScrobbleShows,
                _ => false
            };
        }

        private static bool LongEnough(UserConfig config, PlaybackProgressEventArgs e)
        {
            var runtime = e.MediaInfo?.RunTimeTicks;

            // No runtime info: don't block, let the server decide.
            if (runtime == null)
            {
                return true;
            }

            // MinLength is in minutes; 1 minute == 60 * 10_000_000 ticks.
            return runtime >= 60L * 10_000_000L * config.MinLength;
        }

        private static double GetProgress(PlaybackProgressEventArgs e)
        {
            var position = e.PlaybackPositionTicks;
            var runtime = e.MediaInfo?.RunTimeTicks;
            if (position == null || runtime == null || runtime <= 0)
            {
                return 0d;
            }

            return position.Value / (double)runtime.Value * 100d;
        }

        private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                await HandleStartOrResume(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on PlaybackStart");
            }
        }

        private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (e.Session == null || e.MediaInfo == null)
                {
                    return;
                }

                var sessionId = e.Session.Id;
                var paused = e.IsPaused;

                // First time we see this session (or the item changed): evaluate it once.
                // HandleStartOrResume records the session either way, so we won't land
                // back here every tick.
                if (!_sessions.TryGetValue(sessionId, out var state) || state.ItemId != e.MediaInfo.Id)
                {
                    await HandleStartOrResume(e).ConfigureAwait(false);
                    return;
                }

                // Session already judged not scrobblable: skip silently.
                if (state.Ignore)
                {
                    return;
                }

                // React only to pause/resume transitions — never to plain progress
                // ticks or seeks. Local progress is read fresh from the event each time.
                if (paused && !state.Paused)
                {
                    await SendScrobble(SimklScrobbleAction.Pause, e, GetProgress(e)).ConfigureAwait(false);
                    state.Paused = true;
                }
                else if (!paused && state.Paused)
                {
                    await SendScrobble(SimklScrobbleAction.Start, e, GetProgress(e)).ConfigureAwait(false);
                    state.Paused = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on PlaybackProgress");
            }
        }

        private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                if (e.Session == null || e.MediaInfo == null)
                {
                    return;
                }

                // If the session was judged not scrobblable at start, don't try to
                // close it out (that would log again for a not-logged-in user).
                if (_sessions.TryGetValue(e.Session.Id, out var existing) && existing.Ignore)
                {
                    return;
                }

                // A natural end-of-playback reports 100%; otherwise use the last position.
                var progress = e.PlayedToCompletion ? 100d : GetProgress(e);

                // Always attempt a stop, even if filtered for start, so a paused
                // session is correctly closed out.
                await SendScrobble(SimklScrobbleAction.Stop, e, progress, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on PlaybackStopped");
            }
            finally
            {
                if (e.Session != null)
                {
                    _sessions.TryRemove(e.Session.Id, out _);
                }
            }
        }

        private async Task HandleStartOrResume(PlaybackProgressEventArgs e)
        {
            if (e.Session == null || e.MediaInfo == null)
            {
                return;
            }

            var session = e.Session;

            // Decide eligibility once per session+item. These conditions don't change
            // mid-playback, so caching the decision stops the per-second re-evaluation
            // (and the per-second "not logged in" log spam) that happened when an
            // ineligible session was never recorded.
            var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(session.UserId);
            string? skipReason = null;
            // A profile whose link expired has no token, but its finished
            // watches are kept and replayed once it links again, so its
            // sessions are followed like any other.
            var awaitingRelink = userConfig != null
                                 && string.IsNullOrEmpty(userConfig.UserToken)
                                 && userConfig.LinkExpired;
            if (userConfig == null || (string.IsNullOrEmpty(userConfig.UserToken) && !awaitingRelink))
            {
                skipReason = "user " + session.UserName + " not logged in to Simkl";
            }
            else if (!userConfig.EnablePlaybackScrobbling)
            {
                skipReason = "real-time scrobbling disabled for " + session.UserName;
            }
            else if (!TypeEnabled(userConfig, e.MediaInfo.Type) || !LongEnough(userConfig, e))
            {
                skipReason = "item filtered by type/length";
            }
            else if (_libraryFilter.IsExcluded(userConfig, e.MediaInfo.Path))
            {
                skipReason = "item is in a library excluded by " + session.UserName;
            }

            // Record the session up front, eligible or not, so progress ticks recognise
            // it and stop reprocessing.
            var state = new SessionState
            {
                ItemId = e.MediaInfo.Id,
                Paused = e.IsPaused,
                Ignore = skipReason != null,
                StartProgress = GetProgress(e)
            };
            _sessions[session.Id] = state;

            if (skipReason != null)
            {
                // Logged exactly once per session+item, at debug level so it never
                // floods the main log.
                _logger.LogDebug("Not scrobbling: {Reason}", skipReason);
                return;
            }

            if (!e.IsPaused
                && await SendScrobble(SimklScrobbleAction.Start, e, GetProgress(e)).ConfigureAwait(false))
            {
                state.LastAction = SimklScrobbleAction.Start;
                state.LastSent = DateTime.UtcNow;
            }
        }

        private async Task<bool> SendScrobble(SimklScrobbleAction action, PlaybackProgressEventArgs e, double progress, bool force = false)
        {
            var session = e.Session;
            var mediaInfo = e.MediaInfo;
            if (session == null || mediaInfo == null)
            {
                return false;
            }

            var userId = session.UserId;
            var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            var awaitingRelink = userConfig != null
                                 && string.IsNullOrEmpty(userConfig.UserToken)
                                 && userConfig.LinkExpired;
            if (userConfig == null || (string.IsNullOrEmpty(userConfig.UserToken) && !awaitingRelink))
            {
                _logger.LogDebug("Can't scrobble: user {UserName} not logged in", session.UserName);
                return false;
            }

            if (!userConfig.EnablePlaybackScrobbling)
            {
                return false;
            }

            if (!TypeEnabled(userConfig, mediaInfo.Type) || !LongEnough(userConfig, e))
            {
                return false;
            }

            if (_libraryFilter.IsExcluded(userConfig, mediaInfo.Path))
            {
                return false;
            }

            if (awaitingRelink)
            {
                // Nothing can be sent without a token, but a finished watch is
                // worth keeping: it is replayed with its real date once the
                // user links Simkl again. Live events are simply dropped.
                if (action == SimklScrobbleAction.Stop && progress >= RetryThreshold)
                {
                    QueueForRetry(action, mediaInfo, progress, userId, ResolveSeriesProviderIds(mediaInfo));
                    userConfig.LastScrobble = string.Format(
                        CultureInfo.InvariantCulture,
                        "WAITING: {0} ({1:0.#}%) at {2:yyyy-MM-dd HH:mm} UTC, sent once Simkl is linked again",
                        DisplayName(mediaInfo),
                        progress,
                        DateTime.UtcNow);
                    SimklPlugin.Instance?.SaveConfiguration();
                    _logger.LogInformation(
                        "Kept {Name} for {UserName}: it reaches Simkl once they link their account again",
                        mediaInfo.Name,
                        session.UserName);
                }

                return false;
            }

            // Debounce duplicate calls to dodge the 20s lock. Stop is never debounced.
            if (!force && action != SimklScrobbleAction.Stop
                && _sessions.TryGetValue(session.Id, out var state)
                && state.LastAction == action
                && state.ItemId == mediaInfo.Id
                && DateTime.UtcNow - state.LastSent < _minActionInterval)
            {
                return false;
            }

            try
            {
                _logger.LogInformation(
                    "Scrobble {Action} {Name} ({Progress:0.##}%) for {UserName}",
                    action,
                    mediaInfo.Name,
                    progress,
                    session.UserName);

                var seriesIds = ResolveSeriesProviderIds(mediaInfo);
                var allowRewatch = await ShouldAllowRewatch(action, progress, session.Id, userConfig, mediaInfo.Name)
                    .ConfigureAwait(false);
                var result = await _simklApi.ScrobbleAsync(
                        action, mediaInfo, progress, userConfig.UserToken, seriesIds, allowRewatch)
                    .ConfigureAwait(false);
                var success = result.Success;

                if (success && _sessions.TryGetValue(session.Id, out var s))
                {
                    s.LastAction = action;
                    s.LastSent = DateTime.UtcNow;
                }

                if (!success)
                {
                    QueueForRetry(action, mediaInfo, progress, userId, seriesIds);
                }
                else if (result.Rewatch != null)
                {
                    RecordLastRewatch(userConfig, mediaInfo, result.Rewatch);
                    _logger.LogInformation(
                        "Recorded a rewatch of {Name} (Simkl session {Session}, {State:l})",
                        mediaInfo.Name,
                        result.Rewatch.Id,
                        result.Rewatch.Status ?? "open");
                }

                RecordLastScrobble(userConfig, action, mediaInfo, progress, success, seriesIds);
                return success;
            }
            catch (InvalidTokenException)
            {
                _logger.LogDebug("Deleted invalid user token");
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError(ex, "Couldn't scrobble {Name}", mediaInfo.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught unknown exception while trying to scrobble");
                QueueForRetry(action, mediaInfo, progress, userId, ResolveSeriesProviderIds(mediaInfo));
            }

            return false;
        }

        /// <summary>
        /// Keeps a finished watch that Simkl didn't confirm, so the retry queue
        /// can replay it. Only full watches are worth queuing: anything below the
        /// watched threshold would not have been recorded anyway.
        /// </summary>
        private void QueueForRetry(
            SimklScrobbleAction action,
            MediaBrowser.Model.Dto.BaseItemDto item,
            double progress,
            Guid userId,
            Dictionary<string, string>? seriesProviderIds)
        {
            if (action != SimklScrobbleAction.Stop || progress < RetryThreshold)
            {
                return;
            }

            var isMovie = item.IsMovie == true || item.Type == BaseItemKind.Movie;
            var ids = isMovie ? item.ProviderIds : seriesProviderIds ?? item.ProviderIds;
            if (ids == null || ids.Count == 0)
            {
                // Without ids a replay can't be matched; the filename fallback
                // already had its chance during the live attempt.
                return;
            }

            _retryQueue.Enqueue(new PendingScrobble
            {
                UserId = userId,
                IsMovie = isMovie,
                Name = item.Name,
                Title = isMovie ? item.Name : item.SeriesName,
                Year = item.ProductionYear,
                ProviderIds = new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase),
                Season = isMovie ? null : item.ParentIndexNumber,
                Episode = isMovie ? null : item.IndexNumber,
                FirstFailedUtc = DateTime.UtcNow,
                Attempts = 0
            });
        }

        /// <summary>
        /// Records a short summary of the last scrobble attempt in the user's
        /// configuration, shown on the plugin configuration page.
        /// </summary>
        private static void RecordLastScrobble(
            UserConfig userConfig,
            SimklScrobbleAction action,
            MediaBrowser.Model.Dto.BaseItemDto item,
            double progress,
            bool success,
            Dictionary<string, string>? seriesProviderIds)
        {
            userConfig.LastScrobble = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} - {2} ({3:0.#}%) at {4:yyyy-MM-dd HH:mm} UTC",
                success ? "OK:" : "FAILED:",
                action,
                DisplayName(item),
                progress,
                DateTime.UtcNow);
            userConfig.LastScrobbleUrl = BuildSimklLink(item, seriesProviderIds);
            SimklPlugin.Instance?.SaveConfiguration();
        }

        /// <summary>
        /// Records the last rewatch Simkl accepted, shown on the configuration pages.
        /// </summary>
        private static void RecordLastRewatch(UserConfig userConfig, MediaBrowser.Model.Dto.BaseItemDto item, RewatchSession session)
        {
            userConfig.LastRewatch = string.Format(
                CultureInfo.InvariantCulture,
                "{0} (Simkl session {1}, {2}) at {3:yyyy-MM-dd HH:mm} UTC",
                DisplayName(item),
                session.Id,
                session.IsCompleted ? "complete" : "in progress",
                DateTime.UtcNow);
            SimklPlugin.Instance?.SaveConfiguration();
        }

        private static string DisplayName(MediaBrowser.Model.Dto.BaseItemDto item)
        {
            return string.IsNullOrEmpty(item.SeriesName)
                ? item.Name
                : item.SeriesName + " - " + item.Name;
        }

        /// <summary>
        /// Builds a link to the item on Simkl through Simkl's redirect endpoint,
        /// which resolves IMDb/TMDb/TVDB ids to the right page. Ids only: no
        /// token or client id travels in the URL.
        /// </summary>
        private static string? BuildSimklLink(MediaBrowser.Model.Dto.BaseItemDto item, Dictionary<string, string>? seriesProviderIds)
        {
            var isMovie = item.IsMovie == true || item.Type == BaseItemKind.Movie;
            var ids = isMovie ? item.ProviderIds : seriesProviderIds ?? item.ProviderIds;
            if (ids == null || ids.Count == 0)
            {
                return null;
            }

            var url = new StringBuilder("https://api.simkl.com/redirect?to=Simkl&type=")
                .Append(isMovie ? "movie" : "show");
            var any = false;
            foreach (var key in new[] { "imdb", "tmdb", "tvdb" })
            {
                foreach (var pair in ids)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        url.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(pair.Value));
                        any = true;
                        break;
                    }
                }
            }

            if (!any)
            {
                return null;
            }

            if (!isMovie)
            {
                if (item.ParentIndexNumber != null)
                {
                    url.Append("&season=").Append(item.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (item.IndexNumber != null)
                {
                    url.Append("&episode=").Append(item.IndexNumber.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            return url.ToString();
        }

        /// <summary>
        /// Decides whether this stop may be filed as a rewatch by Simkl. Simkl
        /// itself checks that the item was already watched, keeps the two-day
        /// gap and ignores free plans; the plugin adds the user's opt-in, the
        /// paid-plan check (so no useless flag is sent) and a "really played"
        /// guard: at least half of the item since the session started, so that
        /// resuming near the end or skipping ahead never counts.
        /// </summary>
        private async Task<bool> ShouldAllowRewatch(
            SimklScrobbleAction action,
            double progress,
            string sessionId,
            UserConfig userConfig,
            string name)
        {
            if (action != SimklScrobbleAction.Stop || progress < RetryThreshold || !userConfig.EnableRewatches)
            {
                return false;
            }

            if (_sessions.TryGetValue(sessionId, out var state) && progress - state.StartProgress < MinRewatchSpan)
            {
                _logger.LogInformation(
                    "Not offering {Name} as a rewatch: only {Span:0.#}% of it played this session",
                    name,
                    progress - state.StartProgress);
                return false;
            }

            if (!await IsPaidAccount(userConfig).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Not offering {Name} as a rewatch: Simkl only tracks rewatches on Pro/VIP accounts (this one is {Plan})",
                    name,
                    userConfig.AccountType);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tells whether the user's Simkl plan supports rewatches. The settings
        /// call is activity-gated, so this costs nothing most of the time. An
        /// unknown plan is let through: Simkl then decides.
        /// </summary>
        private async Task<bool> IsPaidAccount(UserConfig userConfig)
        {
            string? plan = null;
            try
            {
                var settings = await _simklApi.GetUserSettings(userConfig.UserToken).ConfigureAwait(false);
                plan = settings?.Account?.Type;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the Simkl plan for the rewatch check");
            }

            plan ??= userConfig.AccountType;
            return string.IsNullOrEmpty(plan)
                   || string.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(plan, "vip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the parent series' provider ids for an episode item.
        /// </summary>
        /// <remarks>
        /// Episodes carry their own IMDB/TVDB/TMDB ids, which are different from
        /// the series-level ids that Simkl needs to identify the show. This method
        /// looks up the parent series via the library and returns its provider ids.
        /// Returns null for non-episode items (movies use their own ids directly).
        /// </remarks>
        private Dictionary<string, string>? ResolveSeriesProviderIds(MediaBrowser.Model.Dto.BaseItemDto item)
        {
            if (item.Type != BaseItemKind.Episode || item.SeriesId == null)
            {
                return null;
            }

            try
            {
                var series = _libraryManager.GetItemById(item.SeriesId.Value);
                var providerIds = series?.ProviderIds;
                if (providerIds is { Count: > 0 })
                {
                    _logger.LogDebug(
                        "Resolved series IDs for {Episode}: {Ids}",
                        item.Name,
                        string.Join(", ", providerIds));
                    return new Dictionary<string, string>(providerIds, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve series for episode {Name}", item.Name);
            }

            return null;
        }

        /// <summary>
        /// Per-session scrobble state.
        /// </summary>
        private sealed class SessionState
        {
            /// <summary>
            /// Gets or sets the id of the item currently playing in this session.
            /// </summary>
            public Guid ItemId { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the session is currently paused.
            /// </summary>
            public bool Paused { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this session has been evaluated
            /// and judged not scrobblable (not logged in, scrobbling disabled, or the
            /// item is filtered). When true the session is tracked but skipped silently
            /// so progress ticks don't re-evaluate or re-log it every second.
            /// </summary>
            public bool Ignore { get; set; }

            /// <summary>
            /// Gets or sets the progress the session started at, used to tell a
            /// real viewing from resuming near the end or skipping ahead.
            /// </summary>
            public double StartProgress { get; set; }

            /// <summary>
            /// Gets or sets the last scrobble action that was successfully sent.
            /// </summary>
            public SimklScrobbleAction LastAction { get; set; }

            /// <summary>
            /// Gets or sets the UTC time of the last successful scrobble.
            /// </summary>
            public DateTime LastSent { get; set; }
        }
    }
}
