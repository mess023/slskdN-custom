// <copyright file="LabelCrateJobsController.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Jobs.API
{
    using System.Threading;
    using System.Threading.Tasks;
    using Asp.Versioning;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using slskd.Authentication;
    using slskd.Core.Security;

    /// <summary>
    ///     Read-only legacy alias. Jobs are created by <see cref="slskd.API.Native.JobsController"/> (POST api/jobs/label-crate);
    ///     a POST here would map the same route twice and fail with an ambiguous match.
    /// </summary>
    [ApiController]
    [Route("api/jobs/label-crate")]
    [Route("api/v{version:apiVersion}/jobs/label-crate")]
    [ApiVersion("0")]
    [Authorize(Policy = AuthPolicy.Any)]
    [ValidateCsrfForCookiesOnly] // CSRF protection for cookie-based auth (exempts JWT/API key)
    [ApiExplorerSettings(IgnoreApi = true)]
    public class LabelCrateJobsController : ControllerBase
    {
        private readonly ILabelCrateJobService jobService;

        public LabelCrateJobsController(ILabelCrateJobService jobService)
        {
            this.jobService = jobService;
        }

        /// <summary>
        ///     Gets a label crate job by ID.
        /// </summary>
        [HttpGet("{jobId}")]
        public async Task<ActionResult<object>> Get(string jobId, CancellationToken ct)
        {
            jobId = jobId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return BadRequest("jobId is required");
            }

            var job = await jobService.GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (job == null)
            {
                return NotFound();
            }

            return Ok(job);
        }
    }
}
