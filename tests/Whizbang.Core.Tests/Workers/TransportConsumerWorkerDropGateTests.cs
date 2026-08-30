using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 3 of plans/pump-then-process.md (Half A) — locks the drop-unsubscribed-types
/// invariant in <see cref="TransportConsumerWorker"/>. Mirror of the gate added to
/// <see cref="ServiceBusConsumerWorker"/>; both consumer paths must drop messages whose
/// inner type has no consumer registered on this service before serializing them into
/// inbox storage.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerDropGateTests {

  /// <summary>Captures the batch handler so the test can deliver simulated batches.</summary>
  private sealed class CapturingBatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination, CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _NopSubscription());
    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotImplementedException();
    public void Dispose() { }

    public async Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) {
      if (_batchHandler is null) {
        throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker — handler not captured.");
      }
      await _batchHandler(batch, CancellationToken.None);
    }

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

  private sealed class FakeReceptorRegistry(bool hasAnyConsumer) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => hasAnyConsumer;
    public bool HasAnyConsumer(string messageType) => hasAnyConsumer;
  }

  /// <summary>
  /// Runtime-registry double — only <see cref="HasAnyRuntimeReceptors"/> is exercised by the
  /// receive-boundary drop gate. The Register/Unregister/GetReceptorsFor methods throw or no-op
  /// because the drop-gate tests never construct receptors through this path.
  /// </summary>
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

  private static MessageEnvelope<JsonElement> _makeEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  // Wrapper envelope-type format the worker expects + the inner type the registry sees.
  private const string WRAPPER_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.UnsubscribedEvent, TestApp]], Whizbang.Core";

  [Test]
  public async Task BatchHandler_RegistrySaysNoConsumer_DropsBeforeStoringInboxAsync() {
    // The slice 3 invariant for TransportConsumerWorker: a message whose inner type has no
    // consumer must never reach StoreInboxMessagesAsync. This locks the drop-gate's "no inbox
    // row created" guarantee for the production-registered consumer worker.
    var transport = new CapturingBatchTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    await using var sp = services.BuildServiceProvider();

    var registry = new FakeReceptorRegistry(hasAnyConsumer: false);
    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: registry,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(150);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
    ]);

    cts.Cancel();

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("Drop gate must skip storage entirely for messages with no local consumer.");
  }

  [Test]
  public async Task BatchHandler_CompileTimeEmptyButRuntimeReceptor_StoresInboxAsync() {
    // Regression lock for the runtime-vs-compile-time gating bug across all three workers
    // (InboxDispatchWorker, ServiceBusConsumerWorker, and now TransportConsumerWorker). The
    // receive-boundary drop gate at the top of the batch handler consults the source-generated
    // IReceptorRegistryQuery.HasAnyConsumer, which only knows about compile-time-declared
    // consumers. A message of a type the service has runtime-registered (integration-test
    // wait helper, dynamic registration) but no compile-time consumer for would be silently
    // dropped at receive — no inbox row, no downstream lifecycle, nothing.
    //
    // The runtime registry's HasAnyRuntimeReceptors closes the gap. This test asserts that
    // when compile-time HasAnyConsumer reports false but the runtime registry reports a
    // matching runtime receptor, the message survives the drop gate and reaches the inbox.
    var transport = new CapturingBatchTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    await using var sp = services.BuildServiceProvider();

    var compileTimeRegistry = new FakeReceptorRegistry(hasAnyConsumer: false);
    // EnvelopeTypeNameHelper.ExtractInnerTypeName returns the inner type's assembly-qualified
    // form (e.g. "TestApp.UnsubscribedEvent, TestApp"). The drop gate passes that string here.
    // The real GeneratedReceptorRegistry normalizes both sides (strips assembly qualifier)
    // before comparing; the test mimics by matching the unqualified-name prefix.
    var runtimeRegistry = new FakeRuntimeReceptorRegistry(
      hasAnyRuntimeReceptors: name => name.StartsWith("TestApp.UnsubscribedEvent", StringComparison.Ordinal));
    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: compileTimeRegistry,
      runtimeReceptorRegistry: runtimeRegistry,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(150);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
    ]);

    cts.Cancel();

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(3)
      .Because("Drop gate must consult the runtime registry too — when a runtime receptor exists for the inner type, the message must NOT be dropped even when compile-time HasAnyConsumer reports false.");
  }

  [Test]
  public async Task BatchHandler_RegistrySaysHasConsumer_StoresInboxNormallyAsync() {
    // Symmetric positive control: when the registry reports a consumer, the gate must NOT
    // drop. Locks against a regression where the gate's logic flips (e.g. accidentally
    // negating the HasAnyConsumer check) and silently drops every message instead.
    var transport = new CapturingBatchTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    await using var sp = services.BuildServiceProvider();

    var registry = new FakeReceptorRegistry(hasAnyConsumer: true);
    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: registry,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(150);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
      new TransportMessage(_makeEnvelope(), WRAPPER_ENVELOPE_TYPE),
    ]);

    cts.Cancel();

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(3)
      .Because("All 3 messages must reach StoreInboxMessagesAsync when the registry reports a consumer.");
  }

  // Marker for a catalog entry stamped IsComposite. Payloads arrive as JsonElement on this path,
  // so the type is never instantiated — it exists only to give the catalog a Type + ClrTypeName.
  private sealed class DropGateCompositeMarker;

  private sealed class CompositeMarkerCatalog : IMessageTypeCatalog {
    private static readonly IReadOnlyList<MessageTypeCatalogEntry> _entries = [
      new(typeof(DropGateCompositeMarker), TypeNameFormatter.FormatClrTypeName(typeof(DropGateCompositeMarker)), "event", null) { IsComposite = true },
    ];
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _entries;
  }

  [Test]
  public async Task BatchHandler_CompositeWireType_NotDroppedByNoConsumerGateAsync() {
    // A composite is WIRE-ONLY: it is an IMessage, never an IEvent, and no service registers a
    // consumer for the composite type itself — consumers exist for its INNER event types, which
    // only become visible after CompositeInboxFanout.TryExpand runs at the dispatch seam. So
    // HasAnyConsumer is false for every composite by construction, and an unexempted gate drops
    // it here, before any inbox row is written. That loses the entire burst with no dead-letter
    // and no recovery path, and the drop is only visible at Debug — silent in any deployment
    // that does not run verbose logging.
    //
    // Same bug shape as the body-offload claim exemption: a wire-only envelope type that no
    // service consumes must survive the gate so the real messages can be recovered downstream.
    var transport = new CapturingBatchTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    await using var sp = services.BuildServiceProvider();

    var compositeEnvelopeType =
      $"Whizbang.Core.Observability.MessageEnvelope`1[[{typeof(DropGateCompositeMarker).AssemblyQualifiedName}]], Whizbang.Core";

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: new FakeReceptorRegistry(hasAnyConsumer: false),
      eventMarkerResolver: new EventMarkerResolver(new CompositeMarkerCatalog()),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(150);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_makeEnvelope(), compositeEnvelopeType),
      new TransportMessage(_makeEnvelope(), compositeEnvelopeType),
      new TransportMessage(_makeEnvelope(), compositeEnvelopeType),
    ]);

    cts.Cancel();

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(3)
      .Because("The no-consumer gate must exempt composites — nothing consumes the composite type itself, " +
               "so dropping here destroys the whole burst before the dispatch seam can fan it out into its inner events.");
  }
}
