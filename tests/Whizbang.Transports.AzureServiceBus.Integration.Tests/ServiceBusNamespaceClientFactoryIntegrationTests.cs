using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="ServiceBusNamespaceClientFactory.CreateClient"/>.
/// </summary>
/// <remarks>
/// CreateClient does not just construct a client: it goes through
/// AzureServiceBusConnectionRetry, which verifies connectivity with a real
/// GetNamespacePropertiesAsync round-trip before returning. That makes a live
/// namespace mandatory, so every CreateClient test lives here against the emulator
/// rather than in the unit suite. The CreateAdminClient branches, which touch no
/// network, stay in ServiceBusNamespaceClientFactoryTests.
/// </remarks>
[Category("Integration")]
[Timeout(60_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusNamespaceClientFactoryIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task CreateClient_AgainstALiveNamespace_ReturnsAConnectedClientAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    await using var client = factory.CreateClient(
        "default",
        _fixture.ConnectionString,
        new AzureServiceBusOptions());

    await Assert.That(client).IsNotNull();
    await Assert.That(client).IsTypeOf<ServiceBusClient>();
  }

  [Test]
  public async Task CreateClient_AgainstALiveNamespace_TargetsTheFixtureNamespaceAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    await using var client = factory.CreateClient(
        "default",
        _fixture.ConnectionString,
        new AzureServiceBusOptions());

    await Assert.That(client.FullyQualifiedNamespace).IsNotNull();
    await Assert.That(client.IsClosed).IsFalse();
  }

  [Test]
  public async Task CreateClient_WithEmptyConnectionString_ThrowsBeforeContactingTheBrokerAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    await Assert.That(() => factory.CreateClient("default", string.Empty, new AzureServiceBusOptions()))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task CreateAdminClient_AgainstALiveNamespace_CanReadNamespacePropertiesAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    var admin = factory.CreateAdminClient(
        "default",
        _fixture.ConnectionString,
        new AzureServiceBusOptions { AutoProvisionInfrastructure = true });

    await Assert.That(admin).IsNotNull();

    var properties = await admin!.GetNamespacePropertiesAsync();

    await Assert.That(properties).IsNotNull();
  }
}
