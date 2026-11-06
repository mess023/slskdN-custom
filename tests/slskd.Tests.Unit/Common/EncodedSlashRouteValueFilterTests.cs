// <copyright file="EncodedSlashRouteValueFilterTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Common;

using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using slskd.Common;
using Xunit;

// Kestrel keeps %2F encoded in the path, so a Soulseek username such as "https://www.rawzik.com"
// reaches {username} actions as "https:%2F%2Fwww.rawzik.com" and every lookup 404s. The filter
// restores the slashes and must leave every other value alone.
public class EncodedSlashRouteValueFilterTests
{
    [Theory]
    [InlineData("https:%2F%2Fwww.rawzik.com", "https://www.rawzik.com")]
    [InlineData("a%2fb", "a/b")]
    [InlineData("plain user", "plain user")]
    [InlineData("100%", "100%")]
    public void Decode_RestoresOnlyEncodedSlashes(string input, string expected)
    {
        Assert.Equal(expected, EncodedSlashRouteValueFilter.Decode(input));
    }

    [Fact]
    public void OnActionExecuting_RewritesUsernameArgument()
    {
        var context = CreateContext(routeUsername: "https:%2F%2Fwww.rawzik.com", argumentUsername: "https:%2F%2Fwww.rawzik.com");

        new EncodedSlashRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("https://www.rawzik.com", context.ActionArguments["username"]);
    }

    [Fact]
    public void OnActionExecuting_IgnoresArgumentsNotBoundFromRoute()
    {
        var context = CreateContext(routeUsername: null, argumentUsername: "a%2Fb");

        new EncodedSlashRouteValueFilter().OnActionExecuting(context);

        Assert.Equal("a%2Fb", context.ActionArguments["username"]);
    }

    private static ActionExecutingContext CreateContext(string? routeUsername, string argumentUsername)
    {
        var routeData = new RouteData();
        if (routeUsername is not null)
        {
            routeData.Values["username"] = routeUsername;
        }

        var actionContext = new ActionContext(new DefaultHttpContext(), routeData, new ActionDescriptor());
        var arguments = new Dictionary<string, object?> { ["username"] = argumentUsername };
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }
}
