// <copyright file="AutomationsController.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.API.Native;

using System;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using slskd.Automations;
using slskd.Core.Security;

/// <summary>
///     Server-side automations: CRUD, (dry) runs and run history. Administrator only, reads included: runs execute
///     with server rights (e.g. Library Health scans, which are administrator-only themselves) and their details
///     expose paths and the Soulseek account.
/// </summary>
[ApiController]
[Route("api/automations")]
[Route("api/v{version:apiVersion}/automations")]
[ApiVersion("0")]
[Produces("application/json")]
[ValidateCsrfForCookiesOnly] // CSRF protection for cookie-based auth (exempts JWT/API key)
[Authorize(Policy = AuthPolicy.Any, Roles = AuthRole.AdministratorOnly)]
public class AutomationsController : ControllerBase
{
    private readonly IAutomationService automations;

    public AutomationsController(IAutomationService automations)
    {
        this.automations = automations;
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await automations.ListAsync());

    [HttpGet("templates")]
    public IActionResult Templates() => Ok(automations.GetTemplates());

    /// <summary>Runs of all automations, newest first.</summary>
    /// <param name="limit">Maximum number of runs (1-1000).</param>
    /// <param name="since">Optional ISO-8601 instant: only runs started at or after it.</param>
    [HttpGet("runs")]
    public async Task<IActionResult> AllRuns([FromQuery] int limit = 50, [FromQuery] DateTimeOffset? since = null)
        => Ok(await automations.ListRunsAsync(null, limit, since));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var automation = await automations.GetAsync(id);
        return automation == null ? NotFound() : Ok(automation);
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<IActionResult> Runs(Guid id, [FromQuery] int limit = 20)
    {
        if (await automations.GetAsync(id) == null)
        {
            return NotFound();
        }

        return Ok(await automations.ListRunsAsync(id, limit));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AutomationRequest request)
    {
        try
        {
            var created = await automations.CreateAsync(request);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] AutomationRequest request)
    {
        try
        {
            var updated = await automations.UpdateAsync(id, request);
            return updated == null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) => await automations.DeleteAsync(id) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(Guid id, [FromQuery] bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var run = await automations.RunAsync(id, dryRun, cancellationToken);
        return run == null ? NotFound() : Ok(run);
    }
}
