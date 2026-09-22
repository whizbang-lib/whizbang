using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Tests for <see cref="ServiceBusNamespaceClientFactory.CreateClient"/>, kept together
/// in the integration project because CreateClient is not a pure construction call.
/// </summary>
/// <remarks>
/// <para>CreateClient goes through AzureServiceBusConnectionRetry, which verifies
/// connectivity with a real <c>GetNamespacePropertiesAsync</c> round-trip before it returns.
/// That is a management-plane call, and the Service Bus emulator this project runs against
/// does not serve one: it reads its topology once at startup from a static Config.json and
/// implements no administration API. A success-path test therefore hangs against the
/// emulator, retrying the probe until the run is killed — it needs a real Azure namespace,
/// which neither local runs nor CI have.</para>
/// <para>What is left is the guard below, which reaches the call site and returns before any
/// network access. The success path stays unverified by design; covering it would mean
/// standing up a live namespace and gating the test on credentials being present.</para>
/// <para>No emulator fixture is taken here deliberately — nothing in this class needs a
/// broker, and requesting the shared fixture would make a millisecond test wait on
/// container startup.</para>
/// </remarks>
[Category("Integration")]
public class ServiceBusNamespaceClientFactoryIntegrationTests {

  [Test]
  public async Task CreateClient_WithEmptyConnectionString_ThrowsBeforeContactingTheBrokerAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    await Assert.That(() => factory.CreateClient("default", string.Empty, new AzureServiceBusOptions()))
        .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task CreateClient_WithNullConnectionString_ThrowsBeforeContactingTheBrokerAsync() {
    var factory = new ServiceBusNamespaceClientFactory(retryLogger: null);

    await Assert.That(() => factory.CreateClient("default", null!, new AzureServiceBusOptions()))
        .ThrowsExactly<ArgumentNullException>();
  }
}
