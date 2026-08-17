using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Stream-integrity Phase B: the origin-side checkpoint publisher advances the watermark through
/// the coordinator (one winner per window) and publishes one <see cref="IntegrityCheckpoint"/>
/// carrying the origin's identity and the window's per-(tenant, type) counts — INCLUDING empty
/// windows, because a missing checkpoint is the consumer's liveness alarm.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
public class IntegrityCheckpointWorkerTests {

  [Test]
  public async Task RunCheckpointOnce_PublishesWindowWithOriginIdentityAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [
          new CheckpointBucket { TenantScope = "tenant-a", EventType = "Contracts.ThingCreated", Count = 3 },
          new CheckpointBucket { TenantScope = null, EventType = "Contracts.ProbeHappened", Count = 1 },
        ]
      }
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
    await Assert.That(checkpoint.CheckpointStreamId).IsEqualTo(coordinator.LocalServiceId)
      .Because("one homogeneous ephemeral checkpoint stream per origin — the stream IS the origin.");
    await Assert.That(checkpoint.OriginServiceName).IsEqualTo("origin-svc")
      .Because("the service NAME is the directed-message Target a consumer repairs through.");
    await Assert.That(checkpoint.FromCommitSequence).IsEqualTo(5L);
    await Assert.That(checkpoint.ToCommitSequence).IsEqualTo(9L);
    await Assert.That(checkpoint.Buckets.Count).IsEqualTo(2);
    await Assert.That(checkpoint.Buckets[0].Count).IsEqualTo(3);
  }

  [Test]
  public async Task RunCheckpointOnce_EmptyWindow_StillPublishesAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 }
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.Buckets).IsEmpty()
      .Because("a quiet window still checkpoints — ABSENCE is the liveness alarm, so silence " +
               "must always be abnormal.");
  }

  [Test]
  public async Task RunCheckpointOnce_NullWindow_PublishesNothingAsync() {
    var coordinator = new _checkpointCoordinator { Window = null };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc");

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("null = unsupported engine OR another instance won this window's advance — " +
               "publishing would double-checkpoint the window.");
  }

  [Test]
  public async Task RunCheckpointOnce_WithTransport_PublishesToOwnEventTopicsAsync() {
    // THE ROUTING FIX: namespace-routing the checkpoint sends it to a control-plane topic no
    // consumer subscribes to (verified live: zero subscriptions — every checkpoint dropped at the
    // broker, origin tracking empty, the deep audit permanently inert). The checkpoint must ride
    // the ORIGIN'S OWN event topics — the ones its consumers already subscribe to — one publish
    // per DISTINCT topic across the origin's audited event types.
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var usersType = typeof(CheckpointTopicProbes.Users.UsersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [
          new CheckpointBucket { TenantScope = "tenant-a", EventType = TypeNameFormatter.Format(ordersType), Count = 3 },
        ]
      },
      // A historically-emitted type absent from this quiet window still gets coverage —
      // consumers of ITS topic need the heartbeat too.
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType), TypeNameFormatter.Format(usersType)],
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc",
      transport: transport, catalog: new _catalog(ordersType, usersType));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var addresses = transport.Published.Select(p => p.Destination.Address).Order().ToList();
    await Assert.That(addresses).IsEquivalentTo([
      "whizbang.core.tests.workers.checkpointtopicprobes.orders",
      "whizbang.core.tests.workers.checkpointtopicprobes.users",
    ]).Because("one checkpoint per DISTINCT topic of the origin's audited event types (window " +
               "buckets UNION historical own-lane types) — exactly the topics its consumers " +
               "already subscribe to.");
    await Assert.That(transport.Published.All(p => p.EnvelopeType!.Contains(nameof(IntegrityCheckpoint)))).IsTrue();
    await Assert.That(transport.Published.All(p =>
        p.Destination.Metadata?["StreamId"].GetString() == coordinator.LocalServiceId.ToString())).IsTrue()
      .Because("session-enabled subscriptions dead-letter sessionless deliveries — every fan-out " +
               "destination must carry the checkpoint stream (the origin id) as its session key.");
    var payload = System.Text.Json.JsonSerializer.Deserialize<IntegrityCheckpoint>(
      ((MessageEnvelope<System.Text.Json.JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      (System.Text.Json.JsonSerializerOptions)JsonContextRegistry.CreateCombinedOptions())!;
    await Assert.That(payload.RequestTopic).IsEqualTo("origin.requests")
      .Because("the checkpoint carries the ORIGIN'S OWN request address (a topic it consumes) — " +
               "the only party that can name an origin-reachable topic is the origin itself.");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("the namespace-routed publish went to a topic with no subscribers — it must be " +
               "replaced, not duplicated.");
  }

  [Test]
  public async Task RunCheckpointOnce_RegistryRoutedHost_PublishesToOwnEventTopicsAsync() {
    // Production hosts may route via the generated ITopicRegistry (+ optional topic routing
    // strategy) with NO IOutboxRoutingStrategy registered — the dispatcher supports both layers,
    // so the checkpoint fan-out must too, or those hosts silently fall back to the dead
    // namespace-routed publish this fix exists to replace.
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var usersType = typeof(CheckpointTopicProbes.Users.UsersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType), TypeNameFormatter.Format(usersType)],
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc",
      transport: transport, catalog: new _catalog(ordersType, usersType),
      outboxRouting: false, topicRegistry: new _topicRegistry(
        (ordersType, "app.orders"), (usersType, "app.users")));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var addresses = transport.Published.Select(p => p.Destination.Address).Order().ToList();
    await Assert.That(addresses).IsEquivalentTo(["app.orders", "app.users"])
      .Because("the fan-out honors the registry + topic-routing layer exactly as the dispatcher " +
               "does when no outbox routing strategy is registered.");
    await Assert.That(transport.Published.All(p =>
        p.Destination.Metadata?["StreamId"].GetString() == coordinator.LocalServiceId.ToString())).IsTrue()
      .Because("the registry-routed path needs the session key just as much as the strategy path.");
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  [Test]
  public async Task RunCheckpointOnce_TransportButNoResolvableTypes_FallsBackToDispatcherAsync() {
    // No catalog (or nothing resolvable) means no topics can be derived — publish through the
    // dispatcher as before rather than silently dropping the heartbeat.
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 }
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, serviceName: "origin-svc",
      transport: transport, catalog: null);

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty();
    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.Buckets).IsEmpty();
  }

  [Test]
  public async Task ControlPlaneDestination_For_StampsSessionAndRoutingKeyAsync() {
    // Shared-inbox subscriptions filter on the message Subject (sys.Label) by namespace — a
    // publish with no routing key gets Subject "message" and is silently dropped by the broker
    // rule (no logs, no DLQ). The destination must carry the "{namespace}.{typename}" Subject.
    var streamId = TrackedGuid.NewMedo().Value;

    var destination = Whizbang.Core.Transports.ControlPlaneDestination.For(
      "inbox", streamId, typeof(IntegrityCheckpoint));

    await Assert.That(destination.Address).IsEqualTo("inbox");
    await Assert.That(destination.RoutingKey).IsEqualTo("whizbang.core.messaging.integritycheckpoint")
      .Because("the Subject is the ONLY thing the shared-inbox broker filter can match.");
    await Assert.That(destination.Metadata?["StreamId"].GetString()).IsEqualTo(streamId.ToString());
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityCheckpointWorker _buildWorker(
      _checkpointCoordinator coordinator, _captureDispatcher dispatcher, string serviceName,
      _captureTransport? transport = null, IMessageTypeCatalog? catalog = null,
      bool outboxRouting = true, ITopicRegistry? topicRegistry = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider(serviceName));
    if (transport is not null) {
      services.AddSingleton<ITransport>(transport);
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
      if (outboxRouting) {
        services.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
      }
      var consumerOptions = new TransportConsumerOptions();
      consumerOptions.Destinations.Add(new TransportDestination("origin.requests"));
      services.AddSingleton(consumerOptions);
    }
    if (topicRegistry is not null) {
      services.AddSingleton(topicRegistry);
    }
    if (catalog is not null) {
      services.AddSingleton(catalog);
    }
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  private sealed class _checkpointCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityCheckpointWindow? Window { get; init; }
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public List<string> OwnAuditedEventTypes { get; init; } = [];

    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<string>>([.. OwnAuditedEventTypes]);

    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Window);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);
  }

  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<object> Published { get; } = [];

    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
  }

  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class _topicRegistry(params (Type Type, string Topic)[] map) : ITopicRegistry {
    public string? GetBaseTopic(Type messageType) =>
      map.Where(m => m.Type == messageType).Select(m => m.Topic).FirstOrDefault();
  }

  private sealed class _catalog(params Type[] eventTypes) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [.. eventTypes.Select(t => new MessageTypeCatalogEntry(t, TypeNameFormatter.Format(t), "event", null))];
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
