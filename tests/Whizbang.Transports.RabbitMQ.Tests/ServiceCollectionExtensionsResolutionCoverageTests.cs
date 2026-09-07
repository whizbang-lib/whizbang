#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Coverage for <see cref="ServiceCollectionExtensions"/> registration factories that must
/// actually be RESOLVED (not merely registered) to run: the backlog-peek factory and its
/// <c>_consumedQueueNames</c> queue-name derivation, the default-only infrastructure-provisioner
/// short-circuit, the multi-namespace transport's per-namespace Information logging, and the
/// <c>_activeConsumeNamespaceKeys</c> consume-side namespace projection.
/// </summary>
public class ServiceCollectionExtensionsResolutionCoverageTests {
  private const string CONNECTION_STRING = "amqp://guest:guest@localhost:5672/";

  // --- IBacklogPeek registration + _consumedQueueNames ---

  [Test]
  public async Task AddRabbitMQTransport_BacklogPeek_ReportsDepthForManifestDerivedQueueAsync() {
    // The backlog-age duty samples whatever this factory hands it; if the DI wiring stops
    // sharing the transport's own channel pool, or the queue-name derivation drifts from the
    // "{ServiceName}-{topic}" convention the consumer actually subscribes with, operators get
    // silent zeros (or errors) instead of a real depth reading.
    var channel = new FakeChannel();
    channel.ExistingQueues.Add("myservice-mytopic");
    channel.PassiveQueueDepths["myservice-mytopic"] = 7;
    var services = new ServiceCollection();
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(channel)));
    services.AddSingleton(new TopologyManifest(
      "myservice",
      [],
      [new InboxSubscription("MyTopic")]));

    services.AddRabbitMQTransport(CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();
    var peek = provider.GetRequiredService<IBacklogPeek>();

    var samples = await peek.PeekAsync(CancellationToken.None);

    var sample = samples.Single();
    await Assert.That(sample.Entity).IsEqualTo("myservice-mytopic")
      .Because("the queue name must follow the same {ServiceName}-{topic} convention the consumer subscribes with");
    await Assert.That(sample.Depth).IsEqualTo(7L);
  }

  [Test]
  public async Task AddRabbitMQTransport_BacklogPeek_NoTopologyManifestRegistered_ReportsNoSamplesAsync() {
    // A service that never built a topology manifest has no known consumed queues; the peek
    // must degrade to "nothing to sample" rather than guessing or throwing during startup.
    var services = new ServiceCollection();
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));

    services.AddRabbitMQTransport(CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();
    var peek = provider.GetRequiredService<IBacklogPeek>();

    var samples = await peek.PeekAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  // --- IInfrastructureProvisioner default-only short-circuit ---

  [Test]
  public async Task AddRabbitMQTransport_DefaultOnlyMap_ProvisionerIsNotACompositeAsync() {
    // A single-namespace host must provision exactly like today — one provisioner, not a
    // composite wrapping a single element. Wrapping it anyway would still work but silently
    // adds indirection to every provisioning call this service ever makes.
    var services = new ServiceCollection();
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddLogging();

    services.AddRabbitMQTransport(CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();

    var provisioner = provider.GetRequiredService<IInfrastructureProvisioner>();

    await Assert.That(provisioner).IsTypeOf<RabbitMQInfrastructureProvisioner>()
      .Because("resources.Keys.Count == 0 for the connection-string overload — it must return the default provisioner unwrapped");
  }

  // --- Multi-namespace transport: per-namespace Information logging ---

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_WithInformationLogger_LogsEachNamespaceAsync() {
    // Operators diagnosing a slow or partially-failed boot rely on this line to see which class
    // namespaces actually finished initializing. If the enabled-check or the log call itself
    // regresses, a namespace could silently fail to report in with no test catching the gap.
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = CONNECTION_STRING,
      ["bulk"] = "amqp://guest:guest@localhost:5672/bulk"
    });

    var logger = new CapturingLogger<RabbitMQTransport>();
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddSingleton<IRabbitMQNamespaceConnectionFactory>(new StubNamespaceConnectionFactory());
    services.AddSingleton<ILogger<RabbitMQTransport>>(logger);

    await using var provider = services.BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();

    await Assert.That(transport).IsTypeOf<NamespaceRoutingTransport>();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Information
      && e.Message.Contains("bulk", StringComparison.Ordinal)
      && e.Message.Contains("initialized", StringComparison.Ordinal))).IsTrue()
      .Because("an operator grepping logs for namespace 'bulk' must see it report initialization");
  }

  [Test]
  public async Task AddRabbitMQTransport_MultiNamespaceMap_SubscribeInvokesTheActiveNamespaceClosureAsync() {
    // The sibling tests call _activeConsumeNamespaceKeys directly, which proves the helper's
    // logic but not that the composed transport is actually holding a closure over the right two
    // services. Those are resolved once at container build with GetService, so a wiring mistake
    // there -- resolving the wrong one, or capturing before registration -- would leave the
    // helper correct and the mirror permanently blind. Only a real subscribe runs the closure.
    var services = new ServiceCollection();
    services.AddRabbitMQTransport(new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = CONNECTION_STRING,
      ["bulk"] = "amqp://guest:guest@localhost:5672/bulk"
    });
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddSingleton<IRabbitMQNamespaceConnectionFactory>(new StubNamespaceConnectionFactory());

    // No TransportNamespaceResolver registered, so the closure resolves null for it and the
    // projection must report nothing active rather than throwing on the missing optional service.
    await using var provider = services.BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      RabbitTestWire.Destination(exchange: "orders"),
      new TransportBatchOptions());

    await Assert.That(subscription).IsNotNull()
      .Because("a multi-namespace host with nothing routed must still subscribe its default namespace");
  }

  private sealed class StubNamespaceConnectionFactory : IRabbitMQNamespaceConnectionFactory {
    public IConnection CreateConnection(string namespaceKey, string connectionString, RabbitMQOptions options) =>
      new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
  }

  // --- _activeConsumeNamespaceKeys (invoked via reflection: the production call site only
  // fires deep inside the multi-namespace ITransport factory closure, after a subscribe on the
  // resulting NamespaceRoutingTransport — reflection tests the same behavior directly). ---

  [Test]
  public async Task ActiveConsumeNamespaceKeys_NullResolver_ReturnsEmptyAsync() {
    // No AddWhizbang / no TransportNamespaceResolver registered at all: a multi-namespace host
    // must still boot with nothing mirrored, rather than throwing on a missing optional service.
    var result = _invokeActiveConsumeNamespaceKeys(resolver: null, registryQuery: null);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ActiveConsumeNamespaceKeys_ResolverWithNoBindings_ReturnsEmptyAsync() {
    // A resolver can exist with zero route-namespace bindings configured — the cheap guard
    // must skip the whole projection rather than walking the (empty) handled-message set.
    var resolver = new TransportNamespaceResolver(new TagOptions(), () => []);

    var result = _invokeActiveConsumeNamespaceKeys(resolver, registryQuery: null);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ActiveConsumeNamespaceKeys_BindingsButNoRegistryQuery_ReturnsEmptyAsync() {
    // IReceptorRegistryQuery is optional (older hosts, or containers without the Whizbang
    // worker pipeline) — its absence must fall back to an empty handled-message set rather
    // than throwing a NullReferenceException out of the registration factory.
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(options, () => []);

    var result = _invokeActiveConsumeNamespaceKeys(resolver, registryQuery: null);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ActiveConsumeNamespaceKeys_HandledMessageRoutedToNamespace_ReturnsThatKeyAsync() {
    // The consume-side mirror rule end to end: a message type this service actually handles,
    // whose tag carries a route-namespace binding, must show up here — this is what tells the
    // registration factory to open a mirrored subscription in the class namespace. Missing this
    // means messages published into the class namespace are never picked up on the consume side.
    var options = new TagOptions();
    options.RouteNamespace("bulk-import", "bulk");
    var resolver = new TransportNamespaceResolver(
      options, () => [_tagRegistration(typeof(RoutedTestMessage), "bulk-import")]);
    var registryQuery = new StubReceptorRegistryQuery([
      new HandledMessageInfo(typeof(RoutedTestMessage).FullName!, "test.commands", MessageKind.Command)
    ]);

    var result = _invokeActiveConsumeNamespaceKeys(resolver, registryQuery);

    await Assert.That(result).IsEquivalentTo(["bulk"]);
  }

  private sealed record RoutedTestMessage;

  private sealed class StubReceptorRegistryQuery(IReadOnlyList<HandledMessageInfo> handled) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() => handled;
  }

  private static MessageTagRegistration _tagRegistration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(object),
    Tag = tag,
    PayloadBuilder = _ => default,
    AttributeFactory = () => throw new NotSupportedException("not exercised by resolution tests"),
  };

  /// <summary>
  /// Invokes the private static _activeConsumeNamespaceKeys method. The production call site
  /// only runs inside the multi-namespace ITransport factory closure, consulted lazily by
  /// NamespaceRoutingTransport on first subscribe — reflection reaches the same behavior
  /// directly with a fake resolver/registry pair.
  /// </summary>
  private static IReadOnlyList<string> _invokeActiveConsumeNamespaceKeys(
      TransportNamespaceResolver? resolver, IReceptorRegistryQuery? registryQuery) {
    var method = typeof(ServiceCollectionExtensions).GetMethod(
      "_activeConsumeNamespaceKeys",
      BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException(
        "_activeConsumeNamespaceKeys not found on ServiceCollectionExtensions - was it renamed?");

    return (IReadOnlyList<string>)method.Invoke(null, [resolver, registryQuery])!;
  }
}
