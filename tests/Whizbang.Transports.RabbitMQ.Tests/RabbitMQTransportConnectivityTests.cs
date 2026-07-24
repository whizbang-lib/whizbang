using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Covers <see cref="RabbitMQTransport.CheckConnectivityAsync"/> — the managed-resource connectivity
/// probe reports the live broker connection state (<c>IConnection.IsOpen</c>), so a connection that
/// dropped after initialization surfaces as unhealthy rather than reading healthy off a stale init flag.
/// </summary>
public class RabbitMQTransportConnectivityTests {

  private static RabbitMQTransport _build(bool isOpen) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: isOpen);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQTransport(connection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
  }

  [Test]
  public async Task CheckConnectivity_OpenConnection_TrueAsync()
    => await Assert.That(await _build(isOpen: true).CheckConnectivityAsync()).IsTrue();

  [Test]
  public async Task CheckConnectivity_ClosedConnection_FalseAsync()
    => await Assert.That(await _build(isOpen: false).CheckConnectivityAsync()).IsFalse();
}
