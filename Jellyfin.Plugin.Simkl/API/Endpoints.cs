using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Simkl.API.Objects;
using Jellyfin.Plugin.Simkl.API.Responses;
using Jellyfin.Plugin.Simkl.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Simkl.API
{
    /// <summary>
    /// The simkl endpoints.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Simkl")]
    public class Endpoints : ControllerBase
    {
        private readonly SimklApi _simklApi;
        private readonly ScrobbleRetryQueue _retryQueue;
        private readonly SimklImportService _importService;

        /// <summary>
        /// Initializes a new instance of the <see cref="Endpoints"/> class.
        /// </summary>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="retryQueue">Instance of the <see cref="ScrobbleRetryQueue"/>.</param>
        /// <param name="importService">Instance of the <see cref="SimklImportService"/>.</param>
        public Endpoints(SimklApi simklApi, ScrobbleRetryQueue retryQueue, SimklImportService importService)
        {
            _simklApi = simklApi;
            _retryQueue = retryQueue;
            _importService = importService;
        }

        /// <summary>
        /// Gets the Simkl import state of a user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The status.</returns>
        [HttpGet("users/import/{userId}/status")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<ImportStatus> GetImportStatus([FromRoute] Guid userId)
        {
            return Ok(_importService.GetStatus(userId));
        }

        /// <summary>
        /// Reads the whole Simkl history of a user and reports what an import would change.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/preview")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> PreviewImport([FromRoute] Guid userId)
        {
            return Ok(await _importService.PreviewAsync(userId).ConfigureAwait(false));
        }

        /// <summary>
        /// Runs the confirmed initial import for a user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/apply")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> ApplyImport([FromRoute] Guid userId)
        {
            return Ok(await _importService.ApplyInitialAsync(userId).ConfigureAwait(false));
        }

        /// <summary>
        /// Applies what changed on Simkl since the last pass for a user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="all">True to apply beyond the cap, after confirmation.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/sync")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> SyncImport([FromRoute] Guid userId, [FromQuery] bool all = false)
        {
            return Ok(await _importService.SyncAsync(userId, manual: true, applyAll: all).ConfigureAwait(false));
        }

        /// <summary>
        /// Lists what an export of a user's played history to Simkl would send.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/export-preview")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> ExportPreview([FromRoute] Guid userId)
        {
            return Ok(await _importService.ExportPreviewAsync(userId).ConfigureAwait(false));
        }

        /// <summary>
        /// Sends a user's played history to Simkl (step 2).
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/export")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> Export([FromRoute] Guid userId)
        {
            return Ok(await _importService.ExportApplyAsync(userId).ConfigureAwait(false));
        }

        /// <summary>
        /// Removes from Simkl what a user's last export sent.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/export-undo")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<ImportReport>> ExportUndo([FromRoute] Guid userId)
        {
            return Ok(await _importService.ExportUndoAsync(userId).ConfigureAwait(false));
        }

        /// <summary>
        /// Reverts the last import pass of a user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The report.</returns>
        [HttpPost("users/import/{userId}/undo")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult<ImportReport> UndoImport([FromRoute] Guid userId)
        {
            return Ok(_importService.UndoLast(userId));
        }

        /// <summary>
        /// Counts the finished watches waiting to reach Simkl for a user,
        /// typically while their Simkl link is expired.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The number of waiting watches.</returns>
        [HttpGet("users/pending/{userId}")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetPendingCount([FromRoute] Guid userId)
        {
            return Ok(new { Count = _retryQueue.CountFor(userId) });
        }

        /// <summary>
        /// Gets the oauth pin.
        /// </summary>
        /// <remarks>
        /// Admin only: this legacy flow hands the Simkl token back to the
        /// browser, so it must not be reachable by regular users — they have
        /// the <c>Simkl/Me</c> endpoints, which keep the token server-side.
        /// </remarks>
        /// <returns>The oauth pin.</returns>
        [HttpGet("oauth/pin")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<CodeResponse?>> GetPin()
        {
            return await _simklApi.GetCode();
        }

        /// <summary>
        /// Gets the status for the code.
        /// </summary>
        /// <remarks>
        /// Admin only: once the PIN is approved this response carries the Simkl
        /// access token, so guessing an active code must not be enough to
        /// obtain someone else's token.
        /// </remarks>
        /// <param name="userCode">The user auth code.</param>
        /// <returns>The code status response.</returns>
        [HttpGet("oauth/pin/{userCode}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<CodeStatusResponse?>> GetPinStatus([FromRoute] string userCode)
        {
            return await _simklApi.GetCodeStatus(userCode);
        }

        /// <summary>
        /// Gets the settings for the user.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The user settings.</returns>
        [HttpGet("users/settings/{userId}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<UserSettings?>> GetUserSettings([FromRoute] Guid userId)
        {
            var userConfiguration = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            if (userConfiguration == null)
            {
                return NotFound();
            }

            return await _simklApi.GetUserSettings(userConfiguration.UserToken);
        }

        /// <summary>
        /// Gets the Simkl watch statistics for the user, passed through as raw JSON.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>The user's Simkl statistics.</returns>
        [HttpGet("users/stats/{userId}")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult> GetUserStats([FromRoute] Guid userId)
        {
            var userConfiguration = SimklPlugin.Instance?.Configuration.GetByGuid(userId);
            if (userConfiguration == null || string.IsNullOrEmpty(userConfiguration.UserToken))
            {
                return NotFound();
            }

            var raw = await _simklApi.GetUserStatsRaw(userConfiguration.UserToken);
            if (raw == null)
            {
                return NotFound();
            }

            return Content(raw, "application/json");
        }
    }
}
