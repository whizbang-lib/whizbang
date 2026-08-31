using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>Test event for the slice 3 drop-gate suite — top-level so the JsonSerializerContext source generator can fill in its abstract members.</summary>
internal sealed record DropGateTestEvent(string Value);

[JsonSerializable(typeof(DropGateTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<DropGateTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal sealed partial class DropGateTestJsonContext : JsonSerializerContext { }

/// <summary>
/// Slice 3 of plans/pump-then-process.md (Half A) — partial completion: locks the
/// drop-unsubscribed-types invariant at the receive boundary. When the local service
/// has no consumer (no inbox handler, no lifecycle receptor, no perspective, no tag-
/// attribute) for an envelopeType, ServiceBusConsumerWorker MUST drop the message
/// before storing the inbox row. Without the drop, a consumer's service accumulates
/// wh_inbox rows for cross-service event types it knows nothing about.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerWorkerDropGateTests {

  /// <summary>Captures the BATCH handler the worker registers via SubscribeBatchAsync, so
  /// the test can deliver a single-message batch and exercise the slice 3 drop gate without
  /// spinning up a real transport.</summary>
  private sealed class CapturingTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? BatchHandler { get; private set; }
    public bool IsInitialized { get; private set; }
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      IsInitialized = true;
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _NopSubscription());

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      BatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotImplementedException();

    public void Dispose() { }

    private sealed class _NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  /// <summary>Strategy double that counts QueueInboxMessage calls — proves the drop gate
  /// kept the message out of the storage path (or didn't, in the negative test).</summary>
  private sealed class CountingStrategy : IWorkCoordinatorStrategy {
    public int QueueInboxMessageCalls;
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) => Interlocked.Increment(ref QueueInboxMessageCalls);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class FakeReceptorRegistry(bool hasAnyConsumer) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => hasAnyConsumer;
    public bool HasAnyConsumer(string messageType) => hasAnyConsumer;
  }

  /// <summary>Runtime-registry double for the slice-3 receive-boundary drop-gate tests.
  /// Only <see cref="HasAnyRuntimeReceptors"/> is exercised; the registration paths throw if
  /// touched so the test surface stays narrow.</summary>
  private sealed class FakeRuntimeReceptorRegistry(Func<string, bool>? hasAnyRuntimeReceptors = null) : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Drop-gate tests use the HasAnyRuntimeReceptors delegate; no real receptor registration.");
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage
      => throw new NotSupportedException("Drop-gate tests use the HasAnyRuntimeReceptors delegate; no real receptor registration.");
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool HasAnyRuntimeReceptors(string messageType) => hasAnyRuntimeReceptors?.Invoke(messageType) ?? false;
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>Catalog double stamping <see cref="DropGateTestEvent"/> as a composite, so the
  /// receive gate can recognise it by wire type name the way the real generated catalog does.</summary>
  private sealed class CompositeMarkerCatalog : IMessageTypeCatalog {
    private static readonly IReadOnlyList<MessageTypeCatalogEntry> _entries = [
      new(typeof(DropGateTestEvent), TypeNameFormatter.FormatClrTypeName(typeof(DropGateTestEvent)), "event", null) { IsComposite = true },
    ];
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
  }

  private static (
      ServiceBusConsumerWorker worker,
      CapturingTransport transport,
      CountingStrategy strategy,
      ServiceProvider sp)
    _buildWorker(IReceptorRegistryQuery? registry, IReceptorRegistry? runtimeRegistry = null,
                 IEnvelopeSerializer? envelopeSerializer = null,
                 IEventMarkerResolver? eventMarkerResolver = null) {
    var transport = new CapturingTransport();
    var strategy = new CountingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new FakeServiceInstanceProvider());
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = DropGateTestJsonContext.Default };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      jsonOptions: jsonOptions,
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(),
      options: new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("test-topic", "test-sub")],
      },
      envelopeSerializer: envelopeSerializer,
      receptorRegistry: registry,
      runtimeReceptorRegistry: runtimeRegistry,
      eventMarkerResolver: eventMarkerResolver,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    return (worker, transport, strategy, sp);
  }

  private static MessageEnvelope<DropGateTestEvent> _makeEnvelope() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new MessageEnvelope<DropGateTestEvent> {
      MessageId = MessageId.From(msgId),
      Payload = new DropGateTestEvent("hi"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
    };
  }

  /// <summary>
  /// Build a wrapper envelope-type string in the format the worker expects (and that
  /// _extractMessageTypeFromEnvelopeType parses), using a real CLR type's
  /// AssemblyQualifiedName. The inner type is what the registry compares against.
  /// </summary>
  private static string _makeWrapperEnvelopeType()
    => typeof(MessageEnvelope<DropGateTestEvent>).AssemblyQualifiedName ?? throw new InvalidOperationException("AQN missing");

  [Test]
  public async Task HandleMessage_RegistrySaysNoConsumer_DropsMessage_NoInboxQueueAsync() {
    // Registry reports no consumer for the inner message type → gate fires and the message
    // never reaches QueueInboxMessage. This is the load-bearing slice 3 invariant.
    var registry = new FakeReceptorRegistry(hasAnyConsumer: false);
    var (worker, transport, strategy, sp) = _buildWorker(registry);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

      await Assert.That(transport.BatchHandler).IsNotNull()
        .Because("Worker must register a batch handler with the transport during StartAsync.");

      await transport.BatchHandler!.Invoke(
        [new TransportMessage(_makeEnvelope(), _makeWrapperEnvelopeType())],
        cts.Token);

      await Assert.That(strategy.QueueInboxMessageCalls).IsEqualTo(0)
        .Because("Drop gate must skip storage entirely for messages with no local consumer.");

      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task HandleMessage_BodyOffloadClaim_NotDroppedByNoConsumerGateAsync() {
    // Body-offload (claim-check): a claim's wire type is MessageEnvelope<BodyClaimEnvelopePayload>, which
    // NO service consumes. The no-consumer gate must NOT drop it — that would kill every offloaded message
    // before rehydration. The claim must proceed past the gate (rehydration restores the original type).
    var registry = new FakeReceptorRegistry(hasAnyConsumer: false);
    var (worker, transport, strategy, sp) = _buildWorker(registry, envelopeSerializer: new StubEnvelopeSerializer());
    await using (sp) {
      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

      var claimEnvelopeType = typeof(MessageEnvelope<BodyClaimEnvelopePayload>).AssemblyQualifiedName!;
      await transport.BatchHandler!.Invoke(
        [new TransportMessage(_makeEnvelope(), claimEnvelopeType)],
        cts.Token);

      await Assert.That(strategy.QueueInboxMessageCalls).IsEqualTo(1)
        .Because("The no-consumer gate must exempt offloaded claim envelopes so they proceed past the gate — no service consumes BodyClaimEnvelopePayload, but the real message is rehydrated downstream.");

      await worker.StopAsync(CancellationToken.None);
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName)
      => throw new NotImplementedException();
  }

  // Positive control (gate doesn't drop messages with consumers) and null-registry
  // pass-through are implicitly covered by the 161-test TransportConsumerWorker* suite —
  // its drop-gate-positive test exercises the symmetric invariant on a path that doesn't
  // require IEnvelopeSerializer. The 49-test ServiceBusConsumerWorker* suite covers the
  // null-registry pass-through. Both would break if slice 3's gate became unconditional.

  [Test]
  public async Task HandleMessage_CompileTimeEmptyButRuntimeReceptor_DoesNotDropAsync() {
    // Regression lock for the compile-time-vs-runtime gating gap at the receive boundary.
    // Same bug shape as the TransportConsumerWorker drop-gate fix: HasAnyConsumer is
    // source-generated and only knows about compile-time-declared consumers, so a message
    // type with no compile-time consumer but a runtime-registered receptor would be silently
    // dropped before the inbox row is written. The gate must also consult IReceptorRegistry's
    // HasAnyRuntimeReceptors. SBC is no longer on the prod path (TransportConsumerWorker
    // replaced it) but the symmetry keeps the legacy worker honest for anyone still
    // constructing it directly.
    var compileTimeRegistry = new FakeReceptorRegistry(hasAnyConsumer: false);
    var runtimeRegistry = new FakeRuntimeReceptorRegistry(
      hasAnyRuntimeReceptors: name => name.Contains("DropGateTestEvent", StringComparison.Ordinal));
    // Real EnvelopeSerializer so the message can flow past the gate into QueueInboxMessage —
    // the existing negative tests don't need this because the gate drops before serialization.
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = DropGateTestJsonContext.Default };
    var envelopeSerializer = new EnvelopeSerializer(jsonOptions);
    var (worker, transport, strategy, sp) = _buildWorker(compileTimeRegistry, runtimeRegistry, envelopeSerializer);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

      await Assert.That(transport.BatchHandler).IsNotNull()
        .Because("Worker must register a batch handler with the transport during StartAsync.");

      await transport.BatchHandler!.Invoke(
        [new TransportMessage(_makeEnvelope(), _makeWrapperEnvelopeType())],
        cts.Token);

      await Assert.That(strategy.QueueInboxMessageCalls).IsEqualTo(1)
        .Because("Drop gate must consult runtime registry too — when a runtime receptor exists for the inner type, the message must NOT be dropped even when compile-time HasAnyConsumer reports false.");

      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task HandleMessage_CompositeWireType_NotDroppedByNoConsumerGateAsync() {
    // Composites are wire-only (IMessage, never IEvent): no service registers a consumer for the
    // composite type itself — its consumers are registered for the INNER event types, which only
    // exist after the dispatch seam fans the composite out. HasAnyConsumer is therefore false for
    // every composite by construction, and an unexempted gate drops it before any inbox row is
    // written, losing the whole burst with no dead-letter and no recovery path. Mirrors the
    // TransportConsumerWorker lock so both receive paths stay honest.
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = DropGateTestJsonContext.Default };
    var (worker, transport, strategy, sp) = _buildWorker(
      new FakeReceptorRegistry(hasAnyConsumer: false),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      eventMarkerResolver: new EventMarkerResolver(new CompositeMarkerCatalog()));
    await using (sp) {
      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

      await transport.BatchHandler!.Invoke(
        [new TransportMessage(_makeEnvelope(), _makeWrapperEnvelopeType())],
        cts.Token);

      await Assert.That(strategy.QueueInboxMessageCalls).IsEqualTo(1)
        .Because("The no-consumer gate must exempt composites — nothing consumes the composite type itself, " +
                 "so dropping here destroys the whole burst before the dispatch seam can fan it out into its inner events.");

      await worker.StopAsync(CancellationToken.None);
    }
  }
}
