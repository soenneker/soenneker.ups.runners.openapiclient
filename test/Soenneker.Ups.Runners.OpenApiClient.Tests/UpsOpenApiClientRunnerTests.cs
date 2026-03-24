using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Ups.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class UpsOpenApiClientRunnerTests : FixturedUnitTest
{
    public UpsOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
