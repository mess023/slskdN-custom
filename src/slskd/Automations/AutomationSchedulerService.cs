// <copyright file="AutomationSchedulerService.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Automations;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Ticks every 30 seconds and runs the automations that are due.
/// </summary>
public class AutomationSchedulerService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IAutomationService automations;
    private readonly ILogger<AutomationSchedulerService> logger;

    public AutomationSchedulerService(IAutomationService automations, ILogger<AutomationSchedulerService> logger)
    {
        this.automations = automations;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var runs = await automations.RunDueAsync(DateTimeOffset.UtcNow, stoppingToken);
                    foreach (var run in runs)
                    {
                        logger.LogInformation("Automation {Name} ({Type}): {Status} - {Summary}", run.AutomationName, run.Type, run.Status, run.Summary);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Automation scheduler tick failed");
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
