// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="TransportConsumerWorker"/>: the metrics-registered side of the
/// owned-echo discards, an owned domain configured with a trailing separator, the known-event filter
/// with no metrics, a poison verdict on a host missing the generation or instance provider, an
/// envelope whose payload is null, and the post-lifecycle fallback with no detached-task tracker.
/// </summary>
/// <remarks>
/// Each worker test waits on <see cref="TransportConsumerWorker.WaitForSubscriptionsReadyAsync"/>,
/// which completes from inside the worker body once it has subscribed, so the batch handler the
/// test then drives is provably the one the running worker installed.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerBranchCoverageTests {
  private const string THIS_SERVICE = "OrderService";
  private const string DEDUPLICATED = "whizbang.transport.inbox.messages_deduplicated";
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

  private static readonly string _ownedEventType =
    typeof(BranchOwnedEvent).FullName + ", " + typeof(BranchOwnedEvent).Assembly.GetName().Name;
  private static readonly string _ownedCommandType =
    typeof(BranchOwnedCommand).FullName + ", " + typeof(BranchOwnedCommand).Assembly.GetName().Name;

  internal sealed class BranchOwnedEvent : IEvent;
  internal sealed class BranchOwnedCommand : ICommand;
  internal sealed record BranchKnownEvent(string Name);
  internal sealed record BranchUnknownEvent(string Name);
  internal sealed record BranchNullablePayload(string Name);

  // ---- owned echo discards with metrics -------------------------------------------------------

  [Test]
  public async Task OwnedEventEcho_WithMetricsAndTrailingSeparatorDomain_IsDiscardedAndCountedAsync() {
    // The owned domain ends in '.', so it is used as the namespace prefix as-is; the event's
    // namespace sits under it, making the event an owned echo.
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _services(coordinator, new StaticEventTypeProvider(typeof(BranchOwnedEvent)));
    var transport = new CapturingTransport();
    var worker = _buildWorker(transport, sp, metrics,
      new RoutingOptions().OwnDomains("Whizbang.Core.Tests."), new NamedInstanceProvider(THIS_SERVICE));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_wait);
      await transport.DeliverAsync([new TransportMessage(_typedEnvelope(new BranchOwnedEvent(), "GatewayService"), _ownedEventType)]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("an event under the owned domain prefix is an echo of this service's own publish");
    await Assert.That(_positiveSum(metricHelper, DEDUPLICATED)).IsEqualTo(1d)
      .Because("every echo discard is counted when metrics are registered");
  }

  [Test]
  public async Task OwnedCommandSelfEcho_WithMetrics_IsDiscardedAndCountedAsync() {
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _services(coordinator, new StaticEventTypeProvider(typeof(BranchOwnedEvent)));
    var transport = new CapturingTransport();
    var worker = _buildWorker(transport, sp, metrics,
      new RoutingOptions().OwnDomains(typeof(BranchOwnedCommand).Namespace!), new NamedInstanceProvider(THIS_SERVICE));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_wait);
      await transport.DeliverAsync([new TransportMessage(_typedEnvelope(new BranchOwnedCommand(), THIS_SERVICE), _ownedCommandType)]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("an owned command whose last hop is this service is a self-echo");
    await Assert.That(_positiveSum(metricHelper, DEDUPLICATED)).IsEqualTo(1d)
      .Because("the self-echo discard is counted when metrics are registered");
  }

  // ---- known-event filter without metrics -----------------------------------------------------

  [Test]
  public async Task KnownEventFilter_WithoutMetrics_StillDropsTheUncachedEventAsync() {
    var provider = new GrowingEventTypeProvider([typeof(BranchKnownEvent)]);
    var coordinator = new NoOpWorkCoordinator();
    await using var sp = _services(coordinator, provider);
    var transport = new CapturingTransport();
    var worker = _buildWorker(transport, sp, metrics: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_wait);
      // Phase 1 caches the known set from the provider and stores the known event.
      await transport.DeliverAsync([new TransportMessage(_jsonEnvelope(), _envelopeTypeFor(typeof(BranchKnownEvent)))]);
      // Phase 2: the provider now classifies a second type as an event the cached set lacks.
      provider.EventTypes.Add(typeof(BranchUnknownEvent));
      await transport.DeliverAsync([new TransportMessage(_jsonEnvelope(), _envelopeTypeFor(typeof(BranchUnknownEvent)))]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("the filter drops the uncached event before the insert even with no metrics to count it");
  }

  // ---- poison quarantine on a host missing a provider -----------------------------------------

  [Test]
  public async Task PoisonVerdict_NoGenerationProvider_SkipsTheMoveButStoresTheBatchAsync() {
    var (stored, deadLetters) = await _runPoisonBatchAsync(withGeneration: false, withInstance: true);

    await Assert.That(deadLetters.Moved).IsEmpty()
      .Because("a dead-letter move needs a generation; without one the quarantine is skipped");
    await Assert.That(stored).IsEqualTo(1)
      .Because("skipping the quarantine must not fail the batch");
  }

  [Test]
  public async Task PoisonVerdict_NoInstanceProvider_SkipsTheMoveButStoresTheBatchAsync() {
    var (stored, deadLetters) = await _runPoisonBatchAsync(withGeneration: true, withInstance: false);

    await Assert.That(deadLetters.Moved).IsEmpty()
      .Because("a dead-letter move needs an instance id; without one the quarantine is skipped");
    await Assert.That(stored).IsEqualTo(1);
  }

  [Test]
  public async Task PoisonVerdict_AllProvidersPresent_MovesTheRowAsync() {
    // Control for the two tests above: with every provider present the same verdict moves the row,
    // so the missing provider is what stopped the move there.
    var (_, deadLetters) = await _runPoisonBatchAsync(withGeneration: true, withInstance: true);

    await Assert.That(deadLetters.Moved).Count().IsEqualTo(1);
  }

  // ---- null payload ---------------------------------------------------------------------------

  [Test]
  public async Task NullPayload_SerializesAsObjectAndStoresTheRowAsync() {
    var coordinator = new NoOpWorkCoordinator();
    var serializer = new RecordingSerializer();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
    await using var sp = services.BuildServiceProvider();
    var transport = new CapturingTransport();
    var worker = _buildWorker(transport, sp, metrics: null);
    var envelope = new MessageEnvelope<BranchNullablePayload> {
      MessageId = MessageId.New(),
      Payload = null!,
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_wait);
      await transport.DeliverAsync([new TransportMessage(envelope, _envelopeTypeFor(typeof(BranchNullablePayload)))]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(serializer.MessageTypes.Select(t => t.FullName)).IsEquivalentTo([typeof(object).FullName])
      .Because("with no payload to take a runtime type from, the envelope is serialized as object");
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
  }

  // ---- post-lifecycle fallback with no tracker ------------------------------------------------

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackWithoutTracker_StillFiresBothDetachedStagesAsync() {
    var inline = new StageRecordingInvoker();
    var detached = new StageRecordingInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorInvoker>(detached);
    await using var scopedProvider = services.BuildServiceProvider();
    var eventId = Guid.CreateVersion7();
    var work = new InboxWork {
      MessageId = eventId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(eventId),
        Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      MessageType = "Test.TestEvent, Test"
    };
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, work.Envelope, inline, context, scopedProvider, CancellationToken.None, trackDetachedTask: null);
    await detached.BothDetachedStagesSeen.WaitAsync(_wait);

    await Assert.That(inline.Stages).Contains(LifecycleStage.PostAllPerspectivesInline);
    await Assert.That(inline.Stages).Contains(LifecycleStage.PostLifecycleInline);
    await Assert.That(detached.Stages).Contains(LifecycleStage.PostAllPerspectivesDetached)
      .Because("with no tracker the detached stages are still fired, just not tracked");
    await Assert.That(detached.Stages).Contains(LifecycleStage.PostLifecycleDetached);
  }

  // ---- helpers --------------------------------------------------------------------------------

  private static double _positiveSum(MetricAssertionHelper helper, string name) =>
    helper.GetByName(name).Where(m => m.Value > 0).Sum(m => m.Value);

  private static ServiceProvider _services(NoOpWorkCoordinator coordinator, IEventTypeProvider eventTypes) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(eventTypes);
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    return services.BuildServiceProvider();
  }

  private static TransportConsumerWorker _buildWorker(
      ITransport transport, IServiceProvider sp, TransportMetrics? metrics,
      RoutingOptions? routing = null, IServiceInstanceProvider? instance = null) {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("branch-topic"));
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: instance ?? new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      routingOptions: Options.Create(routing ?? new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()));
  }

  private static async Task<(int Stored, RecordingDeadLetterStore DeadLetters)> _runPoisonBatchAsync(
      bool withGeneration, bool withInstance) {
    var poisoned = Guid.Parse("0199aaaa-bbbb-cccc-dddd-eeeeffff0101");
    var coordinator = new ObservingWorkCoordinator(
      [new InboxRedeliveryObservation(poisoned, 10) { ProcessingAttempts = 10 }]);
    var deadLetters = new RecordingDeadLetterStore();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddScoped<IDeadLetterStore>(_ => deadLetters);
    if (withGeneration) {
      services.AddSingleton<IGenerationProvider>(new StubGenerationProvider());
    }
    if (withInstance) {
      services.AddSingleton<IServiceInstanceProvider>(new NamedInstanceProvider("poison-branch-service"));
    }
    services.AddSingleton<IPoisonMessageDetector>(new PoisonMessageDetector(
      Options.Create(new PoisonMessageOptions { Enabled = true, MaxDurableObservations = 10 }),
      NullLogger<PoisonMessageDetector>.Instance,
      new Meter("Whizbang.Core.Tests.TransportConsumerBranchPoison")));
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    await using var sp = services.BuildServiceProvider();
    var transport = new CapturingTransport();
    var worker = _buildWorker(transport, sp, metrics: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_wait);
      await transport.DeliverAsync([new TransportMessage(_jsonEnvelope(),
        "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Consumed, TestApp]], Whizbang.Core")]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
    return (coordinator.StoredInboxCount, deadLetters);
  }

  private static string _envelopeTypeFor(Type payloadType) =>
    $"Whizbang.Core.Observability.MessageEnvelope`1[[{TypeNameFormatter.Format(payloadType)}]], Whizbang.Core";

  private static MessageEnvelope<JsonElement> _jsonEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static MessageEnvelope<T> _typedEnvelope<T>(T payload, string sourceServiceName) => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = sourceServiceName,
          InstanceId = TrackedGuid.New(),
          HostName = "test-host",
          ProcessId = 1
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
  };

  // ---- fakes ----------------------------------------------------------------------------------

  private sealed class StaticEventTypeProvider(params Type[] types) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => types;
  }

  private sealed class GrowingEventTypeProvider(List<Type> eventTypes) : IEventTypeProvider {
    public List<Type> EventTypes { get; } = eventTypes;
    public IReadOnlyList<Type> GetEventTypes() => [.. EventTypes];
  }

  private sealed class NamedInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = serviceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class StubGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
  }

  /// <summary>Reports a canned set of redelivery observations from the observation-returning store.</summary>
  private sealed class ObservingWorkCoordinator(IReadOnlyList<InboxRedeliveryObservation> observations)
      : NoOpWorkCoordinator, IWorkCoordinator {
    public new int StoredInboxCount { get; private set; }

    Task IWorkCoordinator.StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken) {
      StoredInboxCount += messages.Length;
      return Task.CompletedTask;
    }

    Task<IReadOnlyList<InboxRedeliveryObservation>> IWorkCoordinator.StoreInboxMessagesWithObservationsAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken) {
      StoredInboxCount += messages.Length;
      return Task.FromResult(observations);
    }
  }

  private sealed class RecordingDeadLetterStore : IDeadLetterStore {
    public List<Guid> Moved { get; } = [];

    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moved.Add(sourceId);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  /// <summary>Records the generic type each envelope is serialized as.</summary>
  private sealed class RecordingSerializer : IEnvelopeSerializer {
    private readonly Lock _sync = new();
    private readonly List<Type> _messageTypes = [];

    public List<Type> MessageTypes {
      get {
        lock (_sync) {
          return [.. _messageTypes];
        }
      }
    }

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      lock (_sync) {
        _messageTypes.Add(typeof(TMessage));
      }
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = envelope.Hops?.ToList() ?? [],
      };
      var aqn = typeof(BranchNullablePayload).AssemblyQualifiedName!;
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  /// <summary>Records every stage it is invoked at and signals once both detached post stages arrive.</summary>
  private sealed class StageRecordingInvoker : IReceptorInvoker {
    private readonly Lock _sync = new();
    private readonly List<LifecycleStage> _stages = [];
    private readonly TaskCompletionSource _bothDetached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task BothDetachedStagesSeen => _bothDetached.Task;

    public List<LifecycleStage> Stages {
      get {
        lock (_sync) {
          return [.. _stages];
        }
      }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_sync) {
        _stages.Add(stage);
        if (_stages.Contains(LifecycleStage.PostAllPerspectivesDetached)
            && _stages.Contains(LifecycleStage.PostLifecycleDetached)) {
          _bothDetached.TrySetResult();
        }
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Captures the batch handler the worker subscribes with and delivers batches on demand.</summary>
  private sealed class CapturingTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new QuietSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();

    public Task DeliverAsync(IReadOnlyList<TransportMessage> messages) =>
      _batchHandler is null
        ? throw new InvalidOperationException("No batch handler subscribed yet")
        : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class QuietSubscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release - test double.
    }
  }
}
