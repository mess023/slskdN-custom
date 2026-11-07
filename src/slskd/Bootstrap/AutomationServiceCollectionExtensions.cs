// <copyright file="AutomationServiceCollectionExtensions.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Bootstrap;

using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using slskd.Automations;

public static class AutomationServiceCollectionExtensions
{
    public static IServiceCollection AddSlskdAutomationServices(this IServiceCollection services)
    {
        var connectionString = $"Data Source={Path.Combine(Program.AppDirectory, "automations.db")}";

        services.AddSingleton<AutomationActions>();
        services.AddSingleton<IAutomationService>(sp => new AutomationService(
            connectionString,
            sp.GetRequiredService<AutomationActions>(),
            sp.GetRequiredService<ILogger<AutomationService>>()));
        services.AddHostedService<AutomationSchedulerService>();

        return services;
    }
}
