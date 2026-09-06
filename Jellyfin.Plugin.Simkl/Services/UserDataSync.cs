using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Exceptions;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimklSeason = Jellyfin.Plugin.Simkl.API.Objects.Season;

namespace Jellyfin.Plugin.Simkl.Services
{
    /// <summary>
    /// Pushes manual "mark played" / "mark unplayed" actions to Simkl.
    /// </summary>
    /// <remarks>
    /// Real playback is covered by <see cref="PlaybackScrobbler"/>; this service
    /// covers the check marks: ticking an episode, a season or a movie as played
    /// in the Jellyfin UI. Items are collected for a few seconds before being
    /// sent, so marking a whole season results in a single batched
    /// <c>/sync/history</c> call instead of one call per episode. Episodes are
    /// reported with the series-level provider ids plus season/episode numbers,
    /// which is the form Simkl resolves most reliably.
    /// </remarks>
    public class UserDataSync : IHostedService
    {
        // Sliding window: a flush happens this long after the *last* toggle,
        // so bulk actions (mark season watched) land in one request.
        private static readonly TimeSpan _flushDelay = TimeSpan.FromSeconds(5);

        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<UserDataSync> _logger;
        private readonly SimklApi _simklApi;
        private readonly LibraryFilter _libraryFilter;
        private readonly object _pendingLock = new object();
        private readonly Dictionary<Guid, PendingItems> _pending;
        private readonly Timer _flushTimer;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserDataSync"/> class.
        /// </summary>
        /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{UserDataSync}"/> interface.</param>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="libraryFilter">Instance of the <see cref="LibraryFilter"/>.</param>
        public UserDataSync(
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            ILogger<UserDataSync> logger,
            SimklApi simklApi,
            LibraryFilter libraryFilter)
        {
            _userDataManager = userDataManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _simklApi = simklApi;
            _libraryFilter = libraryFilter;
            _pending = new Dictionary<Guid, PendingItems>();
            _flushTimer = new Timer(OnFlushTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _userDataManager.UserDataSaved += OnUserDataSaved;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
            _flushTimer.Dispose();
            return Task.CompletedTask;
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            try
            {
                // Only manual check marks. Playback-driven marks are already
                // reported by the real-time scrobbler.
                if (e.SaveReason != UserDataSaveReason.TogglePlayed)
                {
                    return;
                }

                var item = e.Item;
                if (item == null || item.IsVirtualItem || (item is not Movie && item is not Episode))
                {
                    return;
                }

                var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(e.UserId);
                if (userConfig == null
                    || string.IsNullOrEmpty(userConfig.UserToken)
                    || !(userConfig.SyncMarkPlayed || userConfig.ImportFromSimkl))
                {
                    return;
                }

                if (item is Movie ? !userConfig.ScrobbleMovies : !userConfig.ScrobbleShows)
                {
                    return;
                }

                if (_libraryFilter.IsExcluded(userConfig, item.Path))
                {
                    return;
                }

                var played = e.UserData.Played;
                // With the continuous sync (step 3) on, unmarks must reach Simkl
                // too, or the next pass would mark the item played again here.
                if (!played && !(userConfig.SyncMarkUnplayed || userConfig.ImportFromSimkl))
                {
                    return;
                }

                lock (_pendingLock)
                {
                    if (!_pending.TryGetValue(e.UserId, out var pending))
                    {
                        pending = new PendingItems();
                        _pending[e.UserId] = pending;
                    }

                    var target = played ? pending.Played : pending.Unplayed;
                    (played ? pending.Unplayed : pending.Played).Remove(item.Id);
                    target[item.Id] = item;

                    // Slide the flush window.
                    _flushTimer.Change(_flushDelay, Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on UserDataSaved");
            }
        }

        private void OnFlushTimer(object? state)
        {
            _ = FlushAsync();
        }

        private async Task FlushAsync()
        {
            try
            {
                Dictionary<Guid, PendingItems> snapshot;
                lock (_pendingLock)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    snapshot = new Dictionary<Guid, PendingItems>(_pending);
                    _pending.Clear();
                }

                foreach (var (userId, pending) in snapshot)
                {
                    var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
                    if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
                    {
                        continue;
                    }

                    await SendBatch(pending.Played.Values, userConfig, remove: false).ConfigureAwait(false);
                    await SendBatch(pending.Unplayed.Values, userConfig, remove: true).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception while flushing mark-played batch");
            }
        }

        private async Task SendBatch(IReadOnlyCollection<BaseItem> items, UserConfig userConfig, bool remove)
        {
            if (items.Count == 0)
            {
                return;
            }

            var history = BuildHistory(items);
            if (history.Movies.Count == 0 && history.Shows.Count == 0)
            {
                return;
            }

            try
            {
                var response = remove
                    ? await _simklApi.RemoveFromHistory(history, userConfig.UserToken).ConfigureAwait(false)
                    : await _simklApi.AddToHistory(history, userConfig.UserToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "{Action} {Movies} movie(s) and {Shows} show batch(es) {Direction} Simkl history ({Response})",
                    remove ? "Removed" : "Added",
                    history.Movies.Count,
                    history.Shows.Count,
                    remove ? "from" : "to",
                    response == null ? "no response" : "ok");
            }
            catch (InvalidTokenException)
            {
                _logger.LogDebug("Deleted invalid user token");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Couldn't sync manual mark-played batch to Simkl");
            }
        }

        /// <summary>
        /// Builds a history payload from library items. Episodes are grouped
        /// per series and sent as show + seasons/episodes numbers with the
        /// series-level provider ids.
        /// </summary>
        private SimklHistory BuildHistory(IReadOnlyCollection<BaseItem> items)
        {
            var history = new SimklHistory();
            var episodesBySeries = new Dictionary<Guid, List<Episode>>();

            foreach (var item in items)
            {
                if (item is Movie movie)
                {
                    var ids = movie.ProviderIds;
                    history.Movies.Add(new SimklMovie
                    {
                        Title = movie.Name,
                        Year = movie.ProductionYear,
                        Ids = ids is { Count: > 0 } ? new SimklMovieIds(ids) : null,
                        WatchedAt = DateTime.UtcNow
                    });
                }
                else if (item is Episode episode)
                {
                    if (episode.ParentIndexNumber == null || episode.IndexNumber == null)
                    {
                        _logger.LogDebug("Skipping {Name}: missing season/episode number", episode.Name);
                        continue;
                    }

                    if (!episodesBySeries.TryGetValue(episode.SeriesId, out var list))
                    {
                        list = new List<Episode>();
                        episodesBySeries[episode.SeriesId] = list;
                    }

                    list.Add(episode);
                }
            }

            foreach (var (seriesId, episodes) in episodesBySeries)
            {
                var series = _libraryManager.GetItemById(seriesId);
                var seriesIds = series?.ProviderIds;
                if (seriesIds is not { Count: > 0 })
                {
                    _logger.LogDebug("Skipping {Count} episode(s): no provider ids on the series", episodes.Count);
                    continue;
                }

                var seasons = episodes
                    .GroupBy(ep => ep.ParentIndexNumber!.Value)
                    .OrderBy(g => g.Key)
                    .Select(g => new SimklSeason
                    {
                        Number = g.Key,
                        Episodes = g.Select(ep => ep.IndexNumber!.Value)
                            .Distinct()
                            .OrderBy(n => n)
                            .Select(n => new ShowEpisode { Number = n })
                            .ToList()
                    })
                    .ToList();

                history.Shows.Add(new SimklShow
                {
                    Title = series?.Name,
                    Year = series?.ProductionYear,
                    Ids = new SimklShowIds(new Dictionary<string, string>(seriesIds, StringComparer.OrdinalIgnoreCase)),
                    Seasons = seasons
                });
            }

            return history;
        }

        /// <summary>
        /// Items toggled by one user, waiting for the batched flush.
        /// </summary>
        private sealed class PendingItems
        {
            /// <summary>
            /// Gets the items marked played, keyed by item id.
            /// </summary>
            public Dictionary<Guid, BaseItem> Played { get; } = new Dictionary<Guid, BaseItem>();

            /// <summary>
            /// Gets the items marked unplayed, keyed by item id.
            /// </summary>
            public Dictionary<Guid, BaseItem> Unplayed { get; } = new Dictionary<Guid, BaseItem>();
        }
    }
}
