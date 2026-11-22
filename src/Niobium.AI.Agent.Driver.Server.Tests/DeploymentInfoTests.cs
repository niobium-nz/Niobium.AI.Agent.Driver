using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Niobium.AI.Agent.Driver.Server;

namespace Niobium.AI.Agent.Driver.Server.Tests;

[TestClass]
public sealed class DeploymentInfoTests
{
    [TestMethod]
    public void HealthReportsTheCommitEmbeddedInThePublishedAssembly()
    {
        IActionResult response = new DeploymentInfo().Run(new DefaultHttpContext().Request);
        OkObjectResult ok = Assert.IsInstanceOfType<OkObjectResult>(response);
        JsonElement payload = JsonSerializer.SerializeToElement(ok.Value);
        string? revision = typeof(DeploymentInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Commit").Value;

        Assert.IsNotNull(revision);
        Assert.MatchesRegex("^[a-f0-9]{40}$", revision);
        Assert.AreEqual("ready", payload.GetProperty("status").GetString());
        Assert.AreEqual(revision, payload.GetProperty("commit").GetString());
    }
}
