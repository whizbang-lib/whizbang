using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Covers <see cref="AzureServiceBusTransport.CheckConnectivityAsync"/> — the managed-resource
/// connectivity probe reports the live client state (<c>!ServiceBusClient.IsClosed</c>), so a client
/// closed/disposed after initialization surfaces as unhealthy rather than reading off a stale init flag.
/// Uses a real emulator-connection-string client (no broker) and disposes it to flip <c>IsClosed</c>.
/// </summary>
public class AzureServiceBusTransportConnectivityTests {
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZWtleQ==;UseDevelopmentEmulator=true";

  private static (AzureServiceBusTransport transport, ServiceBusClient client) _build() {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    return (new AzureServiceBusTransport(client, jsonOptions), client);
  }

  [Test]
  public async Task CheckConnectivity_OpenClient_TrueAsync() {
    var (transport, client) = _build();
    await using (client.ConfigureAwait(false)) {
      await Assert.That(await transport.CheckConnectivityAsync()).IsTrue();
    }
  }

  [Test]
  public async Task CheckConnectivity_ClosedClient_FalseAsync() {
    var (transport, client) = _build();
    await client.DisposeAsync().ConfigureAwait(false); // IsClosed => true
    await Assert.That(await transport.CheckConnectivityAsync()).IsFalse();
  }
}
