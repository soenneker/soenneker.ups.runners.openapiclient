using Soenneker.Tests.HostedUnit;

namespace Soenneker.Ups.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class UpsOpenApiClientRunnerTests : HostedUnitTest
{
    public UpsOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
