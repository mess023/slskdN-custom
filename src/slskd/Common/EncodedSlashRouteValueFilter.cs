// <copyright file="EncodedSlashRouteValueFilter.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>

namespace slskd.Common;

using System;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
///     Restores encoded slashes in <c>username</c> route values.
/// </summary>
/// <remarks>
///     Kestrel leaves <c>%2F</c> undecoded in the request path so that it cannot be mistaken for a
///     segment separator, and MVC route values inherit that. Soulseek usernames may contain slashes
///     (e.g. <c>https://www.rawzik.com</c>), so every <c>{username}</c> endpoint would otherwise look
///     up <c>https:%2F%2Fwww.rawzik.com</c> and answer 404.
/// </remarks>
public sealed class EncodedSlashRouteValueFilter : IActionFilter
{
    private const string ParameterName = "username";

    /// <inheritdoc/>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.RouteData.Values.ContainsKey(ParameterName))
        {
            return;
        }

        if (context.ActionArguments.TryGetValue(ParameterName, out var value) && value is string username)
        {
            context.ActionArguments[ParameterName] = Decode(username);
        }
    }

    /// <inheritdoc/>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>
    ///     Replaces encoded forward slashes with literal ones. Only <c>%2F</c> is touched: everything
    ///     else was already decoded by Kestrel, so a full unescape would double-decode names that
    ///     legitimately contain a percent sign.
    /// </summary>
    /// <param name="value">The route value.</param>
    /// <returns>The route value with slashes restored.</returns>
    public static string Decode(string value)
        => value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
}
