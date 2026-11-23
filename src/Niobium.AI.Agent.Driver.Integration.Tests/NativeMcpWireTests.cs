namespace Niobium.AI.Agent.Driver.Integration.Tests;

[TestClass]
public sealed class NativeMcpWireTests
{
    [TestMethod]
    public async Task ActualFunctionsHostExposesTheNativeToolsAndSafeDisabledResults() =>
        await IntegrationHostFixture.Runtime.VerifyNativeMcpAsync();

    [TestMethod]
    public async Task MandatoryAuthenticationTemplateCompiles() =>
        await IntegrationHostFixture.Runtime.CompileAuthenticationTemplateAsync();
}
