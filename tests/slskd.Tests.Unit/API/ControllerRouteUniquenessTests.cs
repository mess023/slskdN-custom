// <copyright file="ControllerRouteUniquenessTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>

namespace slskd.Tests.Unit.API;

using System.Reflection;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using slskd.API.Native;
using slskd.Common.CodeQuality;
using Xunit;

/// <summary>
///     Two actions with the same HTTP method, route and API version make endpoint routing throw
///     AmbiguousMatchException (HTTP 500) on the first request, not at startup. MVC itself combines each controller's
///     [Route] with its actions' templates here, over the same controller set the app registers.
/// </summary>
public sealed class ControllerRouteUniquenessTests
{
    [Fact]
    public void NoTwoActionsShareHttpMethodRouteAndApiVersion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(JobsController).Assembly));
                foreach (var existing in manager.FeatureProviders.OfType<IApplicationFeatureProvider<ControllerFeature>>().ToList())
                {
                    manager.FeatureProviders.Remove(existing);
                }

                manager.FeatureProviders.Add(new SafeControllerFeatureProvider()); // as in production
            })
            .ConfigureApiBehaviorOptions(options => options.SuppressInferBindingSourcesForParameters = true); // as in production
        using var provider = services.BuildServiceProvider();

        var routes = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template is not null)
            .SelectMany(action =>
                from method in HttpMethods(action)
                from version in ApiVersions(action)
                select (Key: $"{method} {Shape(action.AttributeRouteInfo!.Template!)} (v{version})", Action: $"{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name}"))
            .ToList();
        Assert.Contains(routes, route => route.Key.StartsWith("POST api/v{:apiVersion}/jobs/label-crate ", StringComparison.Ordinal)); // the scan sees the app

        var duplicates = routes
            .GroupBy(route => route.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(route => route.Action).Distinct().Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" | ", group.Select(route => route.Action).Distinct())}")
            .ToList();

        Assert.True(duplicates.Count == 0, "Routes mapped by more than one action (AmbiguousMatchException at runtime):\n" + string.Join("\n", duplicates));
    }

    private static IEnumerable<string> HttpMethods(ControllerActionDescriptor action)
    {
        var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(constraint => constraint.HttpMethods).ToList();
        return methods is { Count: > 0 } ? methods : new[] { "ANY" };
    }

    private static IEnumerable<string> ApiVersions(ControllerActionDescriptor action)
    {
        if (action.ControllerTypeInfo.IsDefined(typeof(ApiVersionNeutralAttribute), inherit: true) ||
            action.MethodInfo.IsDefined(typeof(ApiVersionNeutralAttribute), inherit: true))
        {
            return new[] { "neutral" };
        }

        var mapped = action.MethodInfo.GetCustomAttributes<MapToApiVersionAttribute>(inherit: true).SelectMany(attribute => attribute.Versions).ToList();
        var versions = mapped.Count > 0
            ? mapped
            : action.ControllerTypeInfo.GetCustomAttributes<ApiVersionAttribute>(inherit: true).SelectMany(attribute => attribute.Versions).ToList();
        return versions.Count > 0 ? versions.Select(version => version.ToString()) : new[] { "default" };
    }

    // The matcher ignores parameter names ({id} and {jobId} match the same URLs) but not constraints.
    private static string Shape(string template) => Regex.Replace(template.Trim('/'), @"\{(\*{0,2})[^}:=?]*", "{$1");
}
