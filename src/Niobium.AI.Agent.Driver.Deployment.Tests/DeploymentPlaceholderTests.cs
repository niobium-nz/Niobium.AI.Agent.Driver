namespace Niobium.AI.Agent.Driver.Deployment.Tests;

[TestClass]
public sealed class DeploymentPlaceholderTests
{
    [TestMethod]
    public void DeploymentTestProjectRuns()
    {
        Assert.AreEqual("Niobium.AI.Agent.Driver.Deployment.Tests", typeof(DeploymentPlaceholderTests).Assembly.GetName().Name);
    }
}
