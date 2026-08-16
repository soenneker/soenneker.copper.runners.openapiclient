using Soenneker.Tests.HostedUnit;

namespace Soenneker.Copper.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class CopperOpenApiClientRunnerTests : HostedUnitTest
{
    public CopperOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
