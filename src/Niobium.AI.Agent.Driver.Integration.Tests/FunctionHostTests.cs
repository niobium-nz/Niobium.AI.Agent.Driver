using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Niobium.AI.Agent.Driver.Server;
using Niobium.Platform;

namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class FunctionHostTests
{
    [TestMethod]
    public async Task ConfiguredHostWritesTheHealthResponseAsJson()
    {
        IHost host = IntegrationHostFixture.Host;
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICacheStore>();
        DefaultHttpContext http = new() { RequestServices = scope.ServiceProvider };
        using MemoryStream body = new();
        http.Response.Body = body;
        IActionResult result = new DeploymentInfo().Run(http.Request);
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));

        Assert.AreEqual(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.StartsWith("application/json", http.Response.ContentType);
        body.Position = 0;
        using JsonDocument json = await JsonDocument.ParseAsync(body);
        Assert.AreEqual("ready", json.RootElement.GetProperty("status").GetString());
        Assert.MatchesRegex("^[a-f0-9]{40}$", json.RootElement.GetProperty("commit").GetString());
    }
}
