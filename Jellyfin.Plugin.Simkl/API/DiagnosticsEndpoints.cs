using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Simkl.API
{
    /// <summary>
    /// Diagnostic report for bug reports: versions, a settings summary without
    /// secrets, and the plugin's recent lines from the Jellyfin log.
    /// </summary>
    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("Simkl")]
    public class DiagnosticsEndpoints : ControllerBase
    {
        private const int MaxLines = 2000;
        private const int DefaultLines = 400;
        private const long TailBytes = 4L * 1024 * 1024;

        // Anything that looks like a Simkl token or client id: 40+ hex chars.
        private static readonly Regex _secretLike = new Regex("[0-9a-fA-F]{40,}", RegexOptions.Compiled);

        private readonly IApplicationPaths _applicationPaths;
        private readonly IServerApplicationHost _applicationHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiagnosticsEndpoints"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
        public DiagnosticsEndpoints(IApplicationPaths applicationPaths, IServerApplicationHost applicationHost)
        {
            _applicationPaths = applicationPaths;
            _applicationHost = applicationHost;
        }

        /// <summary>
        /// Builds the diagnostic report.
        /// </summary>
        /// <param name="lines">How many log lines to include, at most.</param>
        /// <returns>The report as text, wrapped in JSON.</returns>
        [HttpGet("logs")]
        public ActionResult GetLogs([FromQuery] int lines = DefaultLines)
        {
            lines = Math.Clamp(lines, 50, MaxLines);
            var report = new StringBuilder();
            report.Append("RK Simkl Scrobbler diagnostic report\n");
            report.Append("Generated: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n");
            report.Append("Plugin: ").Append(SimklPlugin.Instance?.Version?.ToString() ?? "?").Append('\n');
            report.Append("Jellyfin: ").Append(_applicationHost.ApplicationVersionString).Append('\n');
            report.Append("OS: ").Append(Environment.OSVersion.VersionString).Append('\n');

            var configs = SimklPlugin.Instance?.Configuration.UserConfigs;
            if (configs != null)
            {
                report.Append("Profiles: ").Append(configs.Count()).Append('\n');
                foreach (var config in configs)
                {
                    report.Append("  - ").Append(config.Id.ToString("N").Substring(0, 8))
                        .Append(": linked=").Append(!string.IsNullOrEmpty(config.UserToken))
                        .Append(", expired=").Append(config.LinkExpired)
                        .Append(", plan=").Append(config.AccountType ?? "?")
                        .Append(", scrobbling=").Append(config.EnablePlaybackScrobbling)
                        .Append(", movies=").Append(config.ScrobbleMovies)
                        .Append(", shows=").Append(config.ScrobbleShows)
                        .Append(", markPlayed=").Append(config.SyncMarkPlayed)
                        .Append(", markUnplayed=").Append(config.SyncMarkUnplayed)
                        .Append(", rewatches=").Append(config.EnableRewatches)
                        .Append(", sync=").Append(config.ImportFromSimkl)
                        .Append(", step1=").Append(config.ImportInitialDone)
                        .Append(", step2=").Append(config.ExportInitialDone)
                        .Append(", unwatch=").Append(config.ImportUnwatch)
                        .Append(", minLength=").Append(config.MinLength)
                        .Append(", excludedLibraries=").Append(config.ExcludedLibraries?.Length ?? 0)
                        .Append('\n');
                    if (!string.IsNullOrEmpty(config.LastScrobble))
                    {
                        report.Append("    last scrobble: ").Append(config.LastScrobble).Append('\n');
                    }

                    if (!string.IsNullOrEmpty(config.ImportLastReport))
                    {
                        report.Append("    last sync pass: ").Append(config.ImportLastReport).Append('\n');
                    }
                }
            }

            report.Append("\n--- Plugin log lines (newest last, up to ").Append(lines).Append(") ---\n");
            foreach (var line in ReadPluginLogLines(lines))
            {
                report.Append(line).Append('\n');
            }

            return Ok(new { Text = _secretLike.Replace(report.ToString(), "[redacted]") });
        }

        private IEnumerable<string> ReadPluginLogLines(int wanted)
        {
            var collected = new List<string>();
            string logDirectory;
            try
            {
                logDirectory = _applicationPaths.LogDirectoryPath;
            }
            catch (Exception)
            {
                return new[] { "(log directory unavailable)" };
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(logDirectory, "log_*.log")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .Take(2);
            }
            catch (Exception)
            {
                return new[] { "(log directory unreadable: " + logDirectory + ")" };
            }

            // Newest file first, but each file's lines keep their order; stop
            // once enough lines were found.
            var chunks = new List<List<string>>();
            foreach (var file in files)
            {
                var chunk = new List<string>();
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > TailBytes)
                    {
                        stream.Seek(-TailBytes, SeekOrigin.End);
                    }

                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    var first = stream.Position > 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (first)
                        {
                            // Skip the partial line a mid-file seek lands on.
                            first = false;
                            continue;
                        }

                        if (line.Contains("Jellyfin.Plugin.Simkl", StringComparison.Ordinal)
                            || line.Contains("Simkl Scrobbler", StringComparison.Ordinal))
                        {
                            chunk.Add(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    chunk.Add("(could not read " + Path.GetFileName(file) + ": " + ex.GetType().Name + ")");
                }

                chunks.Add(chunk);
                if (chunks.Sum(c => c.Count) >= wanted)
                {
                    break;
                }
            }

            // Older file first in the output.
            chunks.Reverse();
            foreach (var chunk in chunks)
            {
                collected.AddRange(chunk);
            }

            return collected.Count > wanted ? collected.Skip(collected.Count - wanted) : collected;
        }
    }
}
