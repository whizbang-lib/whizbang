// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tests.Workers.StoreFirstOwned;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers.StoreFirstOwned {
  /// <summary>An event in a namespace the receiving service owns.</summary>
  internal sealed class OwnedShipmentEvent : IEvent;

  /// <summary>A command in a namespace the receiving service owns.</summary>
  internal sealed class OwnedShipCommand : ICommand;

  /// <summary>An event the receiving service's catalog learns of after its first batch.</summary>
  internal sealed class LateCatalogEvent : IEvent;
}

namespace Whizbang.Core.Tests.Workers {
  /// <summary>
  /// The transport consumer stores every message it receives before any skip decision, and the decision is recorded
  /// with the row (#1720).
  /// </summary>
  /// <remarks>
  /// A message with no local consumer, one directed at another service, an echo of this service's own event or
  /// command, and an event the known-event filter rejected were each skipped before <c>store_inbox_messages</c>: no
  /// row, a Debug log at most, and a count under <c>messages_deduplicated</c>, which they are not. Each is now stored,
  /// marked with why it was skipped, so the skip is durable and inspectable, and counted under a name that says so.
  /// </remarks>
  /// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
  [NotInParallel("WhizbangBackgroundServiceTests")]
  public class TransportConsumerWorkerStoresBeforeSkippingTests {
    private const string THIS_SERVICE = "ShippingService";
    private const string OTHER_SERVICE = "BillingService";

    [Test]
    public async Task AMessageNoLocalConsumerTakes_IsStoredAsync() {
      var harness = new Harness(hasAnyConsumer: false);
      var envelope = _envelope(lastHopService: OTHER_SERVICE);

      await harness.DeliverAsync(envelope, _envelopeTypeOf<OwnedShipmentEvent>());

      await Assert.That(harness.StoredIds).Contains(envelope.MessageId.Value)
        .Because("the skip is decided after the message is stored, where it is durable and inspectable");
      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsEqualTo(InboxReceiveDiscard.NO_LOCAL_CONSUMER)
        .Because("the row records why it was not processed, so the store writes it settled");
    }

    [Test]
    public async Task AMessageDirectedAtAnotherService_IsStoredAsync() {
      var harness = new Harness(hasAnyConsumer: true);
      var envelope = _envelope(lastHopService: OTHER_SERVICE);
      envelope.Target = OTHER_SERVICE;

      await harness.DeliverAsync(envelope, _envelopeTypeOf<OwnedShipmentEvent>());

      await Assert.That(harness.StoredIds).Contains(envelope.MessageId.Value);
      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsEqualTo(InboxReceiveDiscard.FOREIGN_TARGET)
        .Because("the row records why it was not processed, so the store writes it settled");
    }

    [Test]
    public async Task AnEchoOfThisServicesOwnEvent_IsHeldNotDroppedAsync() {
      var harness = new Harness(hasAnyConsumer: true, ownsNamespace: true, catalog: [typeof(OwnedShipmentEvent)]);
      // Delivered from another instance's hop: an owned event is an echo whoever sent it. The echo check matches the
      // event's own type name.
      var envelope = _envelope(lastHopService: OTHER_SERVICE);

      await harness.DeliverAsync(envelope, typeof(OwnedShipmentEvent).FullName + ", " + typeof(OwnedShipmentEvent).Assembly.GetName().Name);

      // The echo check matches the bare type name, from which no inbox row can be built, so the message goes to
      // dead-letter custody with its body: held, never processed and never dropped.
      await Assert.That(harness.Coordinator.DeadLetterImports.Select(i => i.MessageId)).Contains(envelope.MessageId.Value);
      await Assert.That(harness.StoredIds).DoesNotContain(envelope.MessageId.Value);
    }

    [Test]
    public async Task AnEchoOfThisServicesOwnCommand_IsStoredAsync() {
      var harness = new Harness(hasAnyConsumer: true, ownsNamespace: true, catalog: [typeof(LateCatalogEvent)]);
      var envelope = _envelope(lastHopService: THIS_SERVICE);

      await harness.DeliverAsync(envelope, _envelopeTypeOf<OwnedShipCommand>());

      await Assert.That(harness.StoredIds).Contains(envelope.MessageId.Value);
      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsEqualTo(InboxReceiveDiscard.SELF_ECHO)
        .Because("the row records why it was not processed, so the store writes it settled");
    }

    [Test]
    public async Task AnEventTheKnownEventFilterRejects_IsStoredAsync() {
      // The catalog the filter was built from names one event; by the time the row is classified it names this one too.
      var harness = new Harness(hasAnyConsumer: true, catalog: [typeof(OwnedShipmentEvent)], catalogGrowsWith: typeof(LateCatalogEvent));
      var envelope = _envelope(lastHopService: OTHER_SERVICE);

      await harness.DeliverAsync(envelope, _envelopeTypeOf<LateCatalogEvent>());

      await Assert.That(harness.StoredIds).Contains(envelope.MessageId.Value);
      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsEqualTo(InboxReceiveDiscard.UNKNOWN_EVENT_TYPE)
        .Because("the row records why it was not processed, so the store writes it settled");
    }

    [Test]
    public async Task AnUnconsumedEventTheKnownEventFilterAlsoRejects_KeepsItsFirstReasonAsync() {
      var harness = new Harness(hasAnyConsumer: false, catalog: [typeof(OwnedShipmentEvent)], catalogGrowsWith: typeof(LateCatalogEvent));
      var envelope = _envelope(lastHopService: OTHER_SERVICE);

      await harness.DeliverAsync(envelope, _envelopeTypeOf<LateCatalogEvent>());

      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsEqualTo(InboxReceiveDiscard.NO_LOCAL_CONSUMER)
        .Because("a row records the first reason it was skipped for");
    }

    [Test]
    public async Task AMessageThisServiceProcesses_IsStoredWithNoReasonAsync() {
      var harness = new Harness(hasAnyConsumer: true);
      var envelope = _envelope(lastHopService: OTHER_SERVICE);

      await harness.DeliverAsync(envelope, _envelopeTypeOf<OwnedShipmentEvent>());

      await Assert.That(harness.ReasonFor(envelope.MessageId.Value)).IsNull()
        .Because("only a skipped message is stored settled");
    }

    [Test]
    public async Task ASkip_IsCountedUnderItsReason_NotAsADuplicateAsync() {
      using var meterFactory = new TestMeterFactory();
      var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
      using var meter = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
      var harness = new Harness(hasAnyConsumer: false, metrics: metrics);

      await harness.DeliverAsync(_envelope(lastHopService: OTHER_SERVICE), _envelopeTypeOf<OwnedShipmentEvent>());

      var discarded = meter.GetByName("whizbang.transport.inbox.messages_discarded").Where(m => m.Value > 0).ToList();
      await Assert.That(discarded.Sum(m => m.Value)).IsEqualTo(1d);
      await Assert.That(discarded.Single().Tags["reason"]).IsEqualTo(InboxReceiveDiscard.NO_LOCAL_CONSUMER);
      await Assert.That(meter.GetByName("whizbang.transport.inbox.messages_deduplicated").Where(m => m.Value > 0)).IsEmpty()
        .Because("a skip is not a duplicate");
    }

    // ===== Helpers =====

    private static string _envelopeTypeOf<T>() =>
      EnvelopeTypeNameHelper.Format(typeof(T).FullName + ", " + typeof(T).Assembly.GetName().Name);

    private static MessageEnvelope<JsonElement> _envelope(string lastHopService) => new() {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = JsonElement.Parse("{}"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = lastHopService, InstanceId = Guid.Parse("0198f5d2-0000-7000-8000-000000000001"), HostName = "host", ProcessId = 1,
        },
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };

    /// <summary>A consumer worker over a transport that hands it one batch, and a coordinator that records each row it stores.</summary>
    private sealed class Harness(bool hasAnyConsumer, bool ownsNamespace = false, Type[]? catalog = null,
        Type? catalogGrowsWith = null, TransportMetrics? metrics = null) {
      private readonly Type[] _catalog = catalog ?? [];

      public NoOpWorkCoordinator Coordinator { get; } = new();

      public IReadOnlyList<Guid> StoredIds => [.. Coordinator.StoredMessages.Select(m => m.MessageId)];

      public string? ReasonFor(Guid messageId) => Coordinator.StoredMessages.Single(m => m.MessageId == messageId).DiscardedOnReceive;

      public async Task DeliverAsync(IMessageEnvelope envelope, string envelopeType) {
        var transport = new HandingTransport();
        var options = new TransportConsumerOptions();
        options.Destinations.Add(new TransportDestination("shipping-topic"));
        var services = new ServiceCollection();
        services.TryAddWhizbangDefaults();
        services.AddScoped<IWorkCoordinator>(_ => Coordinator);
        services.AddSingleton<IEventTypeProvider>(new GrowingEventTypeProvider(_catalog, catalogGrowsWith));
        services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
        var routing = new RoutingOptions();
        if (ownsNamespace) {
          routing.OwnDomains(typeof(OwnedShipmentEvent).Namespace!);
        }
        await using var provider = services.BuildServiceProvider();

        var worker = new TransportConsumerWorker(
          transport: transport,
          options: options,
          resilienceOptions: new SubscriptionResilienceOptions(),
          scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
          jsonOptions: new JsonSerializerOptions(),
          orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
          metrics: metrics,
          logger: NullLogger<TransportConsumerWorker>.Instance,
          serviceInstanceProvider: new ServiceInstanceProvider(Guid.Parse("0198f5d2-0000-7000-8000-000000000002"), THIS_SERVICE, "host", 1),
          schemaReadyGate: SchemaReadyGate.AlreadyReady(),
          routingOptions: Options.Create(routing),
          workChannelWriter: new WorkChannelWriter(),
          claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
          receptorRegistry: new ConsumerRegistry(hasAnyConsumer),
          runtimeReceptorRegistry: NullReceptorRegistry.Instance,
          ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
          eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
          controlClass: Options.Create(new ControlClassOptions()));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        try {
          await transport.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));
          await transport.HandAsync([new TransportMessage(envelope, envelopeType)]);
        } finally {
          await cts.CancelAsync();
          await worker.StopAsync(CancellationToken.None);
        }
      }
    }

    private sealed class ConsumerRegistry(bool hasAnyConsumer) : IReceptorRegistryQuery {
      public bool HasReceptors(LifecycleStage stage, string messageType) => false;
      public bool HasInboxHandler(string messageType) => hasAnyConsumer;
      public bool HasAnyConsumer(string messageType) => hasAnyConsumer;
    }

    /// <summary>The service's event catalog; when <c>growsWith</c> is set, every read after the first also names it.</summary>
    private sealed class GrowingEventTypeProvider(Type[] types, Type? growsWith) : IEventTypeProvider {
      private int _reads;
      public IReadOnlyList<Type> GetEventTypes() =>
        Interlocked.Increment(ref _reads) > 1 && growsWith is not null ? [.. types, growsWith] : types;
    }

    private sealed class HandingTransport : ITransport {
      private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _handler;
      private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public Task Subscribed => _subscribed.Task;
      public bool IsInitialized => true;
      public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
      public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
      public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
          ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
      public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
          TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
        _handler = batchHandler;
        _subscribed.TrySetResult();
        return Task.FromResult<ISubscription>(new Subscription());
      }
      public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
          CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
        throw new NotSupportedException();
      public Task HandAsync(IReadOnlyList<TransportMessage> batch) => _handler!(batch, CancellationToken.None);

      private sealed class Subscription : ISubscription {
        public bool IsActive => true;
#pragma warning disable CS0067
        public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public void Dispose() { }
      }
    }
  }
}
