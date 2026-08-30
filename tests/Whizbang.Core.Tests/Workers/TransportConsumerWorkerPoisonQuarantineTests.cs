using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Topology arc phase 8.5 — poison detection LAYER 2, at the inbox store gate.
/// <para>
/// Layer 1 (age, at the transport receive boundary) cannot cover two cases: poison that dies
/// mid-processing rather than mid-lock, and surfaces where no trustworthy broker first-enqueue
/// timestamp exists (RabbitMQ's timestamp is publisher-set). Layer 2 covers both by counting what
/// this service durably observed: the store-side idempotency record already exists per message id
/// and is written on every delivery, so a redelivery loop is visible there even though the broker's
/// own delivery counter never moves on a session-enabled entity.
/// </para>
/// Every context here carries a NULL first-enqueue timestamp on purpose: layer 1 is structurally
/// unable to fire, so a passing test can only be layer 2 doing the work.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerPoisonQuarantineTests {

  private const string CONSUMED_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Consumed, TestApp]], Whizbang.Core";

  [Test]
  public async Task Batch_RedeliveryObservationsPastBound_MovesTheInboxRowToDeadLettersAsync() {
    // The lock: the store reports the same message id observed 10 times AND attempted 10 times;
    // layer 2 quarantines it into the EXISTING dead-letter store, from which the existing recovery
    // flow replays it.
    //
    // ProcessingAttempts is required evidence now. The observation counter counts DELIVERIES, so a
    // broadcast fanned out to more subscriptions than the bound crosses it without any receptor
    // having tried the message — quarantining that destroys a message that never failed. A genuine
    // loop, modelled here, has been attempted and keeps coming back.
    var coordinator = new ObservingWorkCoordinator(
      [new InboxRedeliveryObservation(_hostage, 10) { ProcessingAttempts = 10 }]);
    var deadLetters = new RecordingDeadLetterStore();
    var (worker, transport, sp) = _buildWorker(coordinator, deadLetters, maxDurableObservations: 10);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONSUMED_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).Count().IsEqualTo(1);
      await Assert.That(deadLetters.Moved[0].SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
      await Assert.That(deadLetters.Moved[0].SourceId).IsEqualTo(_hostage);
      await Assert.That(deadLetters.Moved[0].FailureReason)
        .IsEqualTo(MessageFailureReason.PoisonRedeliveryLoop);
    }
  }

  [Test]
  public async Task Batch_RedeliveryObservationsBelowBound_DoesNotQuarantineAsync() {
    var coordinator = new ObservingWorkCoordinator([new InboxRedeliveryObservation(_hostage, 9)]);
    var deadLetters = new RecordingDeadLetterStore();
    var (worker, transport, sp) = _buildWorker(coordinator, deadLetters, maxDurableObservations: 10);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONSUMED_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).IsEmpty();
    }
  }

  [Test]
  public async Task Batch_DetectorDisabled_DoesNotQuarantineAsync() {
    var coordinator = new ObservingWorkCoordinator([new InboxRedeliveryObservation(_hostage, 5_000)]);
    var deadLetters = new RecordingDeadLetterStore();
    var (worker, transport, sp) = _buildWorker(
      coordinator, deadLetters, maxDurableObservations: 10, enabled: false);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONSUMED_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).IsEmpty();
    }
  }

  [Test]
  public async Task Batch_NoDetectorWired_UsesTheObservationFreeStorePathAsync() {
    // Zero behavior change without the policy: the worker must not even ASK the store for
    // observations, so a coordinator that cannot supply them is never on a new code path.
    var coordinator = new ObservingWorkCoordinator([new InboxRedeliveryObservation(_hostage, 10)]);
    var deadLetters = new RecordingDeadLetterStore();
    var (worker, transport, sp) = _buildWorker(coordinator, deadLetters, detector: false);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONSUMED_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).IsEmpty();
      await Assert.That(coordinator.ObservationCallCount).IsEqualTo(0);
      await Assert.That(coordinator.PlainStoreCallCount).IsEqualTo(1);
    }
  }

  [Test]
  public async Task Batch_CoordinatorReportsNoObservations_StoresNormallyAsync() {
    var coordinator = new ObservingWorkCoordinator([]);
    var deadLetters = new RecordingDeadLetterStore();
    var (worker, transport, sp) = _buildWorker(coordinator, deadLetters, maxDurableObservations: 10);

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await transport.SubscribedSignal.Task;

      await transport.SimulateBatchReceivedAsync([new TransportMessage(_envelope(), CONSUMED_ENVELOPE_TYPE)]);
      await cts.CancelAsync();

      await Assert.That(deadLetters.Moved).IsEmpty();
      await Assert.That(coordinator.ObservationCallCount).IsEqualTo(1);
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
    }
  }

  #region Fixtures

  private static readonly Guid _hostage = Guid.Parse("0199aaaa-bbbb-cccc-dddd-eeeeffff0001");

  private static MessageEnvelope<JsonElement> _envelope() => new() {
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

  private static (TransportConsumerWorker Worker, SignallingBatchTransport Transport, ServiceProvider Sp)
      _buildWorker(
      ObservingWorkCoordinator coordinator,
      RecordingDeadLetterStore deadLetters,
      int maxDurableObservations = 10,
      bool enabled = true,
      bool detector = true) {
    var transport = new SignallingBatchTransport();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddScoped<IDeadLetterStore>(_ => deadLetters);
    services.AddSingleton<IGenerationProvider>(new StubGenerationProvider());
    services.AddSingleton<IServiceInstanceProvider>(new StubInstanceProvider());
    if (detector) {
      services.AddSingleton<IPoisonMessageDetector>(new PoisonMessageDetector(
        Options.Create(new PoisonMessageOptions {
          Enabled = enabled,
          MaxDurableObservations = maxDurableObservations,
        }),
        NullLogger<PoisonMessageDetector>.Instance,
        new Meter("Whizbang.Core.Tests.TransportConsumerPoison")));
    }
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: new AlwaysConsumedRegistry(),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    return (worker, transport, sp);
  }

  /// <summary>Registry where every type is consumed, so nothing is dropped before the store.</summary>
  private sealed class AlwaysConsumedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  /// <summary>
  /// Work coordinator that reports a canned set of durable redelivery observations, and records
  /// WHICH store overload the worker chose (the zero-behavior-change lock).
  /// </summary>
  private sealed class ObservingWorkCoordinator(IReadOnlyList<InboxRedeliveryObservation> observations)
      : NoOpWorkCoordinator, IWorkCoordinator {
    public int PlainStoreCallCount { get; private set; }
    public int ObservationCallCount { get; private set; }
    public new int StoredInboxCount { get; private set; }

    // Explicit re-implementation remaps the interface slot on this derived type, so the worker's
    // IWorkCoordinator call lands here rather than on the base no-op.
    Task IWorkCoordinator.StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken) {
      PlainStoreCallCount++;
      StoredInboxCount += messages.Length;
      return Task.CompletedTask;
    }

    Task<IReadOnlyList<InboxRedeliveryObservation>> IWorkCoordinator.StoreInboxMessagesWithObservationsAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken) {
      ObservationCallCount++;
      StoredInboxCount += messages.Length;
      return Task.FromResult(observations);
    }
  }

  private sealed class RecordingDeadLetterStore : IDeadLetterStore {
    public List<(string SourceTable, Guid SourceId, MessageFailureReason FailureReason)> Moved { get; } = [];

    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moved.Add((sourceTable, sourceId, failureReason));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class StubGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Parse("0199aaaa-bbbb-cccc-dddd-eeeeffff0002");
    public string ServiceName => "poison-test-service";
    public string HostName => "poison-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => ServiceInstanceInfo.Unknown;
  }

  /// <summary>
  /// Batch transport that signals when the worker subscribed, so tests await a real completion
  /// signal instead of sleeping.
  /// </summary>
  private sealed class SignallingBatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public TaskCompletionSource SubscribedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination, CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new NopSubscription());

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      SubscribedSignal.TrySetResult();
      return Task.FromResult<ISubscription>(new NopSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();

    public void Dispose() { }

    public Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) =>
      _batchHandler is null
        ? throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker.")
        : _batchHandler(batch, CancellationToken.None);

    private sealed class NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  #endregion
}
