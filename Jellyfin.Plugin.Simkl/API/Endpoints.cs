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

        /// <summary>
        /// Initializes a new instance of the <see cref="Endpoints"/> class.
        /// </summary>
        /// <param name="simklApi">Instance of the <see cref="SimklApi"/>.</param>
        /// <param name="retryQueue">Instance of the <see cref="ScrobbleRetryQueue"/>.</param>
        public Endpoints(SimklApi simklApi, ScrobbleRetryQueue retryQueue)
        {
            _simklApi = simklApi;
            _retryQueue = retryQueue;
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
