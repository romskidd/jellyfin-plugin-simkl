using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.API.Exceptions;
using Jellyfin.Plugin.Simkl.API.Objects;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Simkl.Services
{
    /// <summary>
    /// Replays watches that Simkl never acknowledged.
    /// </summary>
    /// <remarks>
    /// A network blip or a Simkl hiccup on the final <c>stop</c> event used to
    /// lose the watch for good — the most common reason a scrobbler "misses" an
    /// episode. Failed finishes are queued here and replayed through
    /// <c>/sync/history</c>, which marks the item watched directly and isn't
    /// subject to the scrobble endpoint's per-user lock. The queue is written to
    /// disk so a server restart doesn't drop it.
    /// </remarks>
    public class ScrobbleRetryQueue : IHostedService
    {
        private const int MaxEntries = 200;

        private static readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _giveUpAfter = TimeSpan.FromHours(24);

        /// <summary>
        /// How long a watch is kept for a profile whose Simkl link expired,
        /// waiting for the user to link again.
        /// </summary>
        private static readonly TimeSpan _relinkGiveUp = TimeSpan.FromDays(30);

        private readonly SimklApi _simklApi;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<ScrobbleRetryQueue> _logger;
        private readonly List<PendingScrobble> _pending = new List<PendingScrobble>();
        private readonly object _lock = new object();
        private readonly Timer _timer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScrobbleRetryQueue"/> class.
        /// </summary>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{ScrobbleRetryQueue}"/> interface.</param>
        public ScrobbleRetryQueue(
            SimklApi simklApi,
            IApplicationPaths applicationPaths,
            ILogger<ScrobbleRetryQueue> logger)
        {
            _simklApi = simklApi;
            _applicationPaths = applicationPaths;
            _logger = logger;
            _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Load();
            _timer.Change(_retryInterval, _retryInterval);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
            Save();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Counts the watches waiting for a user.
        /// </summary>
        /// <param name="userId">The Jellyfin user.</param>
        /// <returns>How many finished watches are waiting to reach Simkl.</returns>
        public int CountFor(Guid userId)
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var entry in _pending)
                {
                    if (entry.UserId == userId)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Queues a watch Simkl didn't accept, so it can be replayed later.
        /// </summary>
        /// <param name="entry">The watch to replay.</param>
        public void Enqueue(PendingScrobble entry)
        {
            lock (_lock)
            {
                // Same item, same user: keep a single pending entry.
                _pending.RemoveAll(p => p.UserId == entry.UserId
                                        && p.IsMovie == entry.IsMovie
                                        && string.Equals(p.Title, entry.Title, StringComparison.OrdinalIgnoreCase)
                                        && p.Season == entry.Season
                                        && p.Episode == entry.Episode);

                if (_pending.Count >= MaxEntries)
                {
                    _pending.RemoveAt(0);
                }

                _pending.Add(entry);
            }

            _logger.LogInformation(
                "Queued {Name} for a later retry: Simkl didn't confirm the watch",
                entry.Name);
            Save();
        }

        private static string BuildPath(IApplicationPaths paths)
        {
            return Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.Simkl.pending.json");
        }

        private void OnTimer(object? state)
        {
            _ = RetryAllAsync();
        }

        private async Task RetryAllAsync()
        {
            List<PendingScrobble> snapshot;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                snapshot = new List<PendingScrobble>(_pending);
            }

            var done = new List<PendingScrobble>();
            foreach (var entry in snapshot)
            {
                var userConfig = SimklPlugin.Instance?.Configuration.GetByGuid(entry.UserId);
                var awaitingRelink = userConfig != null
                                     && string.IsNullOrEmpty(userConfig.UserToken)
                                     && userConfig.LinkExpired;

                var giveUp = awaitingRelink ? _relinkGiveUp : _giveUpAfter;
                if (DateTime.UtcNow - entry.FirstFailedUtc > giveUp)
                {
                    _logger.LogWarning(
                        "Giving up on {Name}: still not sent after {Hours:0} h",
                        entry.Name,
                        giveUp.TotalHours);
                    done.Add(entry);
                    continue;
                }

                if (userConfig == null || string.IsNullOrEmpty(userConfig.UserToken))
                {
                    if (awaitingRelink)
                    {
                        // Kept until the user links Simkl again.
                        continue;
                    }

                    // Unlinked on purpose: nothing we can do with this one.
                    done.Add(entry);
                    continue;
                }

                entry.Attempts++;
                try
                {
                    var history = BuildHistory(entry);
                    var response = await _simklApi.AddToHistory(history, userConfig.UserToken)
                        .ConfigureAwait(false);

                    if (response != null)
                    {
                        _logger.LogInformation(
                            "Replayed {Name} to Simkl after {Attempts} attempt(s)",
                            entry.Name,
                            entry.Attempts);
                        done.Add(entry);
                    }
                }
                catch (InvalidTokenException)
                {
                    // The user has to link again; drop the backlog for them.
                    done.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Retry for {Name} failed, will try again", entry.Name);
                }
            }

            if (done.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var entry in done)
                    {
                        _pending.Remove(entry);
                    }
                }

                Save();
            }
        }

        private static SimklHistory BuildHistory(PendingScrobble entry)
        {
            var history = new SimklHistory();
            if (entry.IsMovie)
            {
                history.Movies.Add(new SimklMovie
                {
                    Title = entry.Title,
                    Year = entry.Year,
                    Ids = new SimklMovieIds(entry.ProviderIds),
                    WatchedAt = entry.FirstFailedUtc
                });
            }
            else
            {
                history.Shows.Add(new SimklShow
                {
                    Title = entry.Title,
                    Year = entry.Year,
                    Ids = new SimklShowIds(entry.ProviderIds),
                    Seasons = new[]
                    {
                        new Season
                        {
                            Number = entry.Season,
                            Episodes = new[] { new ShowEpisode { Number = entry.Episode } }
                        }
                    }
                });
            }

            return history;
        }

        private void Load()
        {
            try
            {
                var path = BuildPath(_applicationPaths);
                if (!File.Exists(path))
                {
                    return;
                }

                var stored = JsonSerializer.Deserialize<List<PendingScrobble>>(File.ReadAllText(path));
                if (stored == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _pending.Clear();
                    _pending.AddRange(stored.Take(MaxEntries));
                }

                if (stored.Count > 0)
                {
                    _logger.LogInformation("Restored {Count} watch(es) waiting to reach Simkl", stored.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not restore the pending scrobble queue");
            }
        }

        private void Save()
        {
            try
            {
                List<PendingScrobble> snapshot;
                lock (_lock)
                {
                    snapshot = new List<PendingScrobble>(_pending);
                }

                File.WriteAllText(
                    BuildPath(_applicationPaths),
                    JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist the pending scrobble queue");
            }
        }
    }
}
