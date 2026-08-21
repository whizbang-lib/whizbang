#pragma warning disable CA1707 // Test method names can contain underscores

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Registration tests for the multi-namespace RabbitMQ overload (transport traffic classes,
/// topology arc phase 8 — plan resolution 7): one <see cref="IConnection"/> + channel pool per
/// TransportNamespace, publish routed by the key the publish strategy stamped on destination
/// metadata, consume mirrored into every actively handled class namespace, and the LOCKED
/// single-namespace guarantee — the connection-string overload and a <c>default</c>-only map
/// produce byte-identical registrations with zero extra connections.
/// </summary>
/// <remarks>
/// A RabbitMQ TransportNamespace is a whole connection. A separate VHOST on the same broker is
/// the recommended isolation (one AMQP URI per vhost — <c>amqp://host/bulk</c>): it gives the
/// class its own exchange/queue namespace, its own permissions and its own resource alarms
/// without a second cluster.
/// </remarks>
public class RabbitMQNamespaceRoutingRegistrationTests {
  private const string DEFAULT_URI = "amqp://guest:guest@localhost:5672/";
  private const string BULK_VHOST_URI = "amqp://guest:guest@localhost:5672/bulk";

  #region 'default' is required

  [Test]
  public async Task AddRabbitMQTransport_MapWithoutDefaultKey_ThrowsNamingTheKeyAsync() {
    var services = new ServiceCollection();
    var map = new Dictionary<string, string> { ["bulk"] = BULK_VHOST_URI };

    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddRabbitMQTransport(map);
      return Task.CompletedTask;
    });

    await Assert.That(ex!.Message).Contains(TransportNamespaces.DefaultKey);
    await Assert.That(ex.ParamName).IsEqualTo("namespaceConnectionStrings");
  }

  [Test]
  public async Task AddRabbitMQTransport_EmptyMap_ThrowsAsync() {
    var services = new ServiceCollection();

    await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddRabbitMQTransport(new Dictionary<string, string>());
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task AddRabbitMQTransport_NullMap_ThrowsAsync() {
    var services = new ServiceCollection();

    await Assert.ThrowsAsync<ArgumentNullException>(() => {
      services.AddRabbitMQTransport((IReadOnlyDictionary<string, string>)null!);
      return Task.CompletedTask;
    });
  }

  #endregion

  #region Single-namespace guarantee (LOCKED)

  [Test]
  public async Task AddRabbitMQTransport_DefaultOnlyMap_RegistersExactlyTheSingleStringShapeAsync() {
    var viaString = new ServiceCollection();
    viaString.AddRabbitMQTransport(DEFAULT_URI);

    var viaMap = new ServiceCollection();
    viaMap.AddRabbitMQTransport(new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = DEFAULT_URI });

    await Assert.That(viaMap.Count).IsEqualTo(viaString.Count);
    await Assert.That(viaMap.Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}").ToList())
      .IsEquivalentTo(viaString.Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}").ToList());
  }

  [Test]
  public async Task AddRabbitMQTransport_DefaultOnlyMap_RegistersExactlyOneConnectionAsync() {
    var services = new ServiceCollection();

    services.AddRabbitMQTransport(new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = DEFAULT_URI });

    await Assert.That(services.Count(d => d.ServiceType == typeof(IConnection))).IsEqualTo(1);
  }

  [Test]
  public async Task AddRabbitMQTransport_DefaultOnlyMap_TransportIsNotAComposedRouterAsync() {
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = DEFAULT_URI });

    await using var provider = _offline(services).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<ITransport>()).IsTypeOf<RabbitMQTransport>()
      .Because("a single-namespace host must be byte-identical to today — no routing wrapper at all");
  }

  #endregion

  #region Multi-namespace composition

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_ComposesANamespaceRouterAsync() {
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = DEFAULT_URI,
      ["bulk"] = BULK_VHOST_URI
    });

    await using var provider = _offline(services).BuildServiceProvider();
    var router = await Assert.That(provider.GetRequiredService<ITransport>()).IsTypeOf<NamespaceRoutingTransport>();

    await Assert.That(router!.NamespaceKeys).IsEquivalentTo(new[] { TransportNamespaces.DefaultKey, "bulk" });
  }

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_OpensOneConnectionPerNamespaceAsync() {
    // Publish-side parity with ASB: a class namespace is a whole connection (a vhost is the
    // recommended isolation), so every namespace gets its own connection and channel pool.
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = DEFAULT_URI,
      ["bulk"] = BULK_VHOST_URI
    });
    var factory = new RecordingNamespaceConnectionFactory();

    await using var provider = _offline(services, factory).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.Transports.Count).IsEqualTo(2);
    await Assert.That(router.Transports.All(t => t is RabbitMQTransport)).IsTrue();
    await Assert.That(factory.Requested).IsEquivalentTo(new[] { ("bulk", BULK_VHOST_URI) })
      .Because("the DEFAULT namespace keeps using the container's IConnection — only class namespaces are opened here");
  }

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_ResolveRoutesByKeyAsync() {
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = DEFAULT_URI,
      ["bulk"] = BULK_VHOST_URI
    });

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.Resolve("bulk")).IsNotSameReferenceAs(router.Resolve(TransportNamespaces.DefaultKey));
    await Assert.That(router.Resolve("never-configured"))
      .IsSameReferenceAs(router.Resolve(TransportNamespaces.DefaultKey));
  }

  [Test]
  public async Task AddRabbitMQTransport_TransportAndProvisioner_ShareOneConnectionPerNamespaceAsync() {
    // The transport and the provisioner are two views of the SAME broker. Giving each its own
    // connection would double the fleet's connection count for no benefit — and RabbitMQ
    // connections are the expensive resource, unlike a management-plane HTTP client.
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = DEFAULT_URI,
      ["bulk"] = BULK_VHOST_URI
    });
    var factory = new RecordingNamespaceConnectionFactory();

    await using var provider = _offline(services, factory).BuildServiceProvider();
    _ = provider.GetRequiredService<ITransport>();
    _ = provider.GetRequiredService<IInfrastructureProvisioner>();

    await Assert.That(factory.Requested.Count).IsEqualTo(1)
      .Because("one connection per class namespace, opened once and shared");
  }

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_ProvisionsEveryNamespaceAsync() {
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = DEFAULT_URI,
      ["bulk"] = BULK_VHOST_URI
    });

    await using var provider = _offline(services).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IInfrastructureProvisioner>())
      .IsTypeOf<CompositeInfrastructureProvisioner>()
      .Because("the consume-side mirror subscribes the same entity set in each namespace — it has to exist there");
  }

  #endregion

  #region Publish strategy + configuration

  [Test]
  public async Task AddRabbitMQTransport_WiresTheTransportNamespaceSeamOnThePublishStrategyAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang(o => o.Tags.RouteNamespace("bulk-import", "bulk"));
    services.AddRabbitMQTransport(DEFAULT_URI);

    await using var provider = _offline(services).BuildServiceProvider();
    var strategy = provider.GetRequiredService<Whizbang.Core.Workers.IMessagePublishStrategy>();

    var field = typeof(Whizbang.Core.Workers.TransportPublishStrategy)
      .GetField("_transportNamespaces", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    await Assert.That(field!.GetValue(strategy)).IsNotNull();
  }

  [Test]
  public async Task AddRabbitMQTransport_ConfigurationNamespacesOverrideTheCodeMapAsync() {
    // Shape: Whizbang:Transports:RabbitMQ:Namespaces:<key> names a ConnectionStrings entry —
    // the same convention as the ASB side, so one operator playbook covers both brokers.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:rabbit-bulk"] = BULK_VHOST_URI,
        ["Whizbang:Transports:RabbitMQ:Namespaces:bulk"] = "rabbit-bulk"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddRabbitMQTransport(DEFAULT_URI);

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.NamespaceKeys).IsEquivalentTo(new[] { TransportNamespaces.DefaultKey, "bulk" });
  }

  #endregion

  #region Helpers

  /// <summary>
  /// Offline registration idiom: pre-register the ambient IConnection (the retry factory would
  /// block on a live handshake) and a namespace connection factory that mints fakes. Registered
  /// after the transport call so these win the last-registration race.
  /// </summary>
  private static IServiceCollection _offline(
      IServiceCollection services, RecordingNamespaceConnectionFactory? factory = null) {
    services.AddLogging();
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddSingleton<IRabbitMQNamespaceConnectionFactory>(factory ?? new RecordingNamespaceConnectionFactory());
    return services;
  }

  private sealed class RecordingNamespaceConnectionFactory : IRabbitMQNamespaceConnectionFactory {
    public List<(string NamespaceKey, string ConnectionString)> Requested { get; } = [];

    public IConnection CreateConnection(string namespaceKey, string connectionString, RabbitMQOptions options) {
      Requested.Add((namespaceKey, connectionString));
      return new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    }
  }

  #endregion
}
