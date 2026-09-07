using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.API.Responses;
using Jellyfin.Plugin.Simkl.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Simkl.API
{
    /// <summary>
    /// Endpoints letting any Jellyfin user link and configure their own Simkl
    /// account, without needing access to the admin dashboard.
    /// </summary>
    /// <remarks>
    /// Every endpoint derives the profile it acts on from the caller's own
    /// access token — never from a parameter — so a user can only ever read or
    /// change their own link. They are paired with the self-service page served
    /// by <see cref="GetLinkPage"/>.
    /// </remarks>
    [ApiController]
    [Authorize]
    [Route("Simkl")]
    public class SelfServiceEndpoints : ControllerBase
    {
        private readonly SimklApi _simklApi;
        private readonly IAuthorizationContext _authContext;
        private readonly LibraryFilter _libraryFilter;
        private readonly ScrobbleRetryQueue _retryQueue;
        private readonly SimklImportService _importService;
        private readonly IUserManager _userManager;
        private readonly ILogger<SelfServiceEndpoints> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SelfServiceEndpoints"/> class.
        /// </summary>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
        /// <param name="libraryFilter">Instance of the <see cref="LibraryFilter"/>.</param>
        /// <param name="retryQueue">Instance of the <see cref="ScrobbleRetryQueue"/>.</param>
        /// <param name="importService">Instance of the <see cref="SimklImportService"/>.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{SelfServiceEndpoints}"/> interface.</param>
        public SelfServiceEndpoints(
            SimklApi simklApi,
            IAuthorizationContext authContext,
            LibraryFilter libraryFilter,
            ScrobbleRetryQueue retryQueue,
            SimklImportService importService,
            IUserManager userManager,
            ILogger<SelfServiceEndpoints> logger)
        {
            _userManager = userManager;
            _simklApi = simklApi;
            _authContext = authContext;
            _libraryFilter = libraryFilter;
            _retryQueue = retryQueue;
            _importService = importService;
            _logger = logger;
        }

        /// <summary>
        /// Serves the standalone page users open to link their own Simkl account.
        /// </summary>
        /// <remarks>
        /// Anonymous on purpose: the browser has to load the page before its
        /// script can authenticate. The page itself contains no user data and
        /// every call it makes is authenticated.
        /// </remarks>
        /// <returns>The self-service HTML page.</returns>
        [HttpGet("Link")]
        [AllowAnonymous]
        public ActionResult GetLinkPage()
        {
            var fragment = ReadFragment();
            if (fragment == null)
            {
                return NotFound();
            }

            // The fragment carries its own scoped styling; this only supplies the
            // document shell it needs when opened on its own.
            var page = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n"
                       + "<meta charset=\"utf-8\"/>\n"
                       + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>\n"
                       + "<title>Link your Simkl account</title>\n"
                       + "<style>html{color-scheme:dark light}"
                       + "body{margin:0;padding:2.2rem 1.1rem 3rem;background:#101418;color:#f2f4f6;"
                       + "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}"
                       + "@media(prefers-color-scheme:light){body{background:#f4f6f8;color:#16191c}}"
                       + "a{color:#00a4dc}</style>\n</head>\n<body>\n"
                       + fragment
                       + "\n</body>\n</html>";

            return Content(page, "text/html; charset=utf-8");
        }

        /// <summary>
        /// Serves the same page as a bare fragment, for hosts that inject it into
        /// an existing document (the optional Plugin Pages integration).
        /// </summary>
        /// <returns>The self-service HTML fragment.</returns>
        [HttpGet("Link/Fragment")]
        public ActionResult GetLinkFragment()
        {
            var fragment = ReadFragment();
            return fragment == null
                ? NotFound()
                : Content(fragment, "text/html; charset=utf-8");
        }

        /// <summary>
        /// Gets the Simkl link status and options of the calling user.
        /// </summary>
        /// <returns>The caller's status.</returns>
        [HttpGet("Me")]
        public async Task<ActionResult> GetMe()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            var config = SimklPlugin.Instance?.Configuration.GetByGuid(userId.Value);
            var linked = config != null && !string.IsNullOrEmpty(config.UserToken);

            string? simklName = null;
            string? simklPlan = null;
            if (linked)
            {
                var settings = await _simklApi.GetUserSettings(config!.UserToken).ConfigureAwait(false);
                if (string.Equals(settings?.Error, "user_token_failed", StringComparison.Ordinal))
                {
                    // The token was just dropped: re-read the config so the page
                    // shows "link expired" right away.
                    linked = false;
                    config = SimklPlugin.Instance?.Configuration.GetByGuid(userId.Value);
                }
                else
                {
                    simklName = settings?.User?.Name;
                    simklPlan = settings?.Account?.Type;
                }
            }

            return Ok(new
            {
                Linked = linked,
                LinkExpired = config?.LinkExpired ?? false,
                PendingWatches = _retryQueue.CountFor(userId.Value),
                SimklName = simklName,
                SimklPlan = simklPlan,
                LastScrobble = config?.LastScrobble,
                LastScrobbleUrl = config?.LastScrobbleUrl,
                LastRewatch = config?.LastRewatch,
                Options = new SimklUserOptions
                {
                    EnablePlaybackScrobbling = config?.EnablePlaybackScrobbling ?? true,
                    ScrobbleMovies = config?.ScrobbleMovies ?? true,
                    ScrobbleShows = config?.ScrobbleShows ?? true,
                    SyncMarkPlayed = config?.SyncMarkPlayed ?? true,
                    SyncMarkUnplayed = config?.SyncMarkUnplayed ?? false,
                    EnableRewatches = config?.EnableRewatches ?? false,
                    ImportFromSimkl = config?.ImportFromSimkl ?? false,
                    ImportUnwatch = config?.ImportUnwatch ?? false,
                    MinLength = config?.MinLength ?? 5,
                    ExcludedLibraries = config?.ExcludedLibraries ?? Array.Empty<string>()
                }
            });
        }

        /// <summary>
        /// Lists the server's libraries, so the page can offer them as exclusions.
        /// </summary>
        /// <returns>The libraries, as id and name.</returns>
        [HttpGet("Me/Libraries")]
        public async Task<ActionResult> GetLibraries()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            // Only the libraries this user may see: the names of the others are
            // none of their business.
            var user = _userManager.GetUserById(userId.Value);
            var all = user != null && user.HasPermission(PermissionKind.EnableAllFolders);
            var allowed = user == null
                ? new HashSet<Guid>()
                : new HashSet<Guid>(user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
            return Ok(_libraryFilter.GetLibraries()
                .Where(l => l.ItemId != null && (all || (Guid.TryParse(l.ItemId, out var id) && allowed.Contains(id))))
                .Select(l => new { Id = l.ItemId, l.Name })
                .ToArray());
        }

        /// <summary>
        /// Starts the Simkl PIN flow for the calling user.
        /// </summary>
        /// <returns>The PIN code to enter on simkl.com.</returns>
        [HttpPost("Me/Pin")]
        public async Task<ActionResult<CodeResponse?>> StartPin()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            return await _simklApi.GetCode().ConfigureAwait(false);
        }

        /// <summary>
        /// Polls the PIN flow and, once approved, stores the token on the
        /// calling user's own profile.
        /// </summary>
        /// <param name="userCode">The PIN being polled.</param>
        /// <returns>Whether the account is now linked.</returns>
        [HttpGet("Me/Pin/{userCode}")]
        public async Task<ActionResult> PollPin([FromRoute] string userCode)
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            // Simkl PIN codes are short alphanumerics; refuse anything else
            // before it goes anywhere near an outgoing URL.
            if (string.IsNullOrEmpty(userCode)
                || userCode.Length > 16
                || !userCode.All(char.IsLetterOrDigit))
            {
                return BadRequest();
            }

            var status = await _simklApi.GetCodeStatus(userCode).ConfigureAwait(false);
            if (status == null)
            {
                return Ok(new { Linked = false, Pending = false });
            }

            if (!string.Equals(status.Result, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new { Linked = false, Pending = true });
            }

            var plugin = SimklPlugin.Instance;
            if (plugin == null || string.IsNullOrEmpty(status.AccessToken))
            {
                return Ok(new { Linked = false, Pending = false });
            }

            var linked = plugin.Configuration.GetOrCreate(userId.Value);
            if (!string.Equals(linked.UserToken, status.AccessToken, StringComparison.Ordinal))
            {
                linked.ForgetCachedAccount();
            }

            linked.UserToken = status.AccessToken;
            linked.LinkExpired = false;
            plugin.SaveConfiguration();
            _logger.LogInformation("Simkl account linked by user {UserId} from the self-service page", userId);

            return Ok(new { Linked = true, Pending = false });
        }

        /// <summary>
        /// Gets the Simkl import state of the calling user.
        /// </summary>
        /// <returns>The status.</returns>
        [HttpGet("Me/Import/Status")]
        public async Task<ActionResult<ImportStatus>> GetImportStatus()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(_importService.GetStatus(userId.Value));
        }

        /// <summary>
        /// Reads the whole Simkl history and reports what an import would change, without touching anything.
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/Preview")]
        public async Task<ActionResult<ImportReport>> PreviewImport()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.PreviewAsync(userId.Value).ConfigureAwait(false));
        }

        /// <summary>
        /// Runs the confirmed initial import.
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/Apply")]
        public async Task<ActionResult<ImportReport>> ApplyImport()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.ApplyInitialAsync(userId.Value).ConfigureAwait(false));
        }

        /// <summary>
        /// Applies what changed on Simkl since the last pass.
        /// </summary>
        /// <param name="all">True to apply beyond the cap, after the user confirmed.</param>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/Sync")]
        public async Task<ActionResult<ImportReport>> SyncImport([FromQuery] bool all = false)
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.SyncAsync(userId.Value, manual: true, applyAll: all).ConfigureAwait(false));
        }

        /// <summary>
        /// Lists what an export of the played history to Simkl would send.
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/ExportPreview")]
        public async Task<ActionResult<ImportReport>> ExportPreview()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.ExportPreviewAsync(userId.Value).ConfigureAwait(false));
        }

        /// <summary>
        /// Sends the played history to Simkl (step 2).
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/Export")]
        public async Task<ActionResult<ImportReport>> Export()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.ExportApplyAsync(userId.Value).ConfigureAwait(false));
        }

        /// <summary>
        /// Removes from Simkl what the last export sent.
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/ExportUndo")]
        public async Task<ActionResult<ImportReport>> ExportUndo()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(await _importService.ExportUndoAsync(userId.Value).ConfigureAwait(false));
        }

        /// <summary>
        /// Turns the continuous sync (step 3) on or off for the calling user, effective at once.
        /// </summary>
        /// <param name="on">True to turn it on.</param>
        /// <returns>The sync status after the change.</returns>
        [HttpPost("Me/Import/Keep")]
        public async Task<ActionResult<ImportStatus>> KeepInSync([FromQuery] bool on)
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(_importService.SetKeepInSync(userId.Value, on));
        }

        /// <summary>
        /// Reverts the last import pass.
        /// </summary>
        /// <returns>The report.</returns>
        [HttpPost("Me/Import/Undo")]
        public async Task<ActionResult<ImportReport>> UndoImport()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            return userId == null ? Unauthorized() : Ok(_importService.UndoLast(userId.Value));
        }

        /// <summary>
        /// Unlinks the calling user's Simkl account.
        /// </summary>
        /// <returns>No content.</returns>
        [HttpPost("Me/Unlink")]
        public async Task<ActionResult> Unlink()
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            var plugin = SimklPlugin.Instance;
            if (plugin != null)
            {
                var config = plugin.Configuration.GetOrCreate(userId.Value);
                config.UserToken = string.Empty;
                config.LinkExpired = false;
                config.ForgetCachedAccount();
                plugin.SaveConfiguration();
            }

            return NoContent();
        }

        /// <summary>
        /// Saves the calling user's own scrobbling options.
        /// </summary>
        /// <param name="options">The options to store.</param>
        /// <returns>No content.</returns>
        [HttpPost("Me/Options")]
        public async Task<ActionResult> SaveOptions([FromBody] SimklUserOptions options)
        {
            var userId = await GetCallerId().ConfigureAwait(false);
            if (userId == null)
            {
                return Unauthorized();
            }

            var plugin = SimklPlugin.Instance;
            if (plugin == null)
            {
                return NoContent();
            }

            var config = plugin.Configuration.GetOrCreate(userId.Value);
            config.EnablePlaybackScrobbling = options.EnablePlaybackScrobbling;
            config.ScrobbleMovies = options.ScrobbleMovies;
            config.ScrobbleShows = options.ScrobbleShows;
            config.SyncMarkPlayed = options.SyncMarkPlayed || config.ImportFromSimkl;
            config.SyncMarkUnplayed = options.SyncMarkUnplayed || config.ImportFromSimkl;
            config.EnableRewatches = options.EnableRewatches;
            config.ImportUnwatch = options.ImportUnwatch;
            config.MinLength = Math.Clamp(options.MinLength, 0, 600);
            var known = new HashSet<string>(
                _libraryFilter.GetLibraries().Where(l => l.ItemId != null).Select(l => l.ItemId!),
                StringComparer.OrdinalIgnoreCase);
            config.ExcludedLibraries = (options.ExcludedLibraries ?? Array.Empty<string>())
                .Where(known.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            plugin.SaveConfiguration();

            return NoContent();
        }

        /// <summary>
        /// Reads the embedded self-service markup.
        /// </summary>
        private static string? ReadFragment()
        {
            var stream = typeof(SelfServiceEndpoints).Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.Simkl.Configuration.linkPage.html");
            if (stream == null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Resolves the Jellyfin user behind the current request, or null when
        /// the request carries an API key rather than a real user session.
        /// </summary>
        private async Task<Guid?> GetCallerId()
        {
            var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (auth.User == null || auth.UserId.Equals(default))
            {
                return null;
            }

            return auth.UserId;
        }
    }
}
