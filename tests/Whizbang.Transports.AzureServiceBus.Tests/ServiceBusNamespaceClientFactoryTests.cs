using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for the internal <see cref="ServiceBusNamespaceClientFactory"/>.
/// </summary>
/// <remarks>
/// Only the admin-client branch is covered here. CreateClient verifies connectivity
/// through GetNamespacePropertiesAsync before it returns, so every test that calls it
/// lives in ServiceBusNamespaceClientFactoryIntegrationTests against the emulator.
/// </remarks>
public class ServiceBusNamespaceClientFactoryTests {
  private const string FAKE_CONNECTION_STRING =
      "Endpoint=sb://whizbang-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dGVzdC1rZXktbm90LXJlYWw=";

  [Test]
  public async Task CreateAdminClient_WhenAutoProvisionDisabled_ReturnsNullAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);
    var options = new AzureServiceBusOptions { AutoProvisionInfrastructure = false };

    var admin = factory.CreateAdminClient("default", FAKE_CONNECTION_STRING, options);

    await Assert.That(admin).IsNull();
  }

  [Test]
  public async Task CreateAdminClient_WhenAutoProvisionEnabled_ReturnsWrapperAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);
    var options = new AzureServiceBusOptions { AutoProvisionInfrastructure = true };

    var admin = factory.CreateAdminClient("default", FAKE_CONNECTION_STRING, options);

    await Assert.That(admin).IsNotNull();
    await Assert.That(admin).IsTypeOf<ServiceBusAdminClientWrapper>();
  }

  [Test]
  public async Task CreateAdminClient_DefaultOptions_ProvisionsByDefaultAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    var admin = factory.CreateAdminClient("default", FAKE_CONNECTION_STRING, new AzureServiceBusOptions());

    await Assert.That(admin).IsNotNull();
  }
}
