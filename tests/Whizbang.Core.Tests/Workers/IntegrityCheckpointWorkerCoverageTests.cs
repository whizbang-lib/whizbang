using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Round-23 coverage: <see cref="IntegrityCheckpointWorker"/> lines the existing
/// <c>IntegrityCheckpointWorkerTests.cs</c> suite does not reach — the schema-gate cancellation
/// return, the OperationCanceledException loop-break, and the four branches inside
/// <c>_tryPublishToOwnEventTopicsAsync</c> that skip an unresolvable type / decide there is
/// nothing to fan out to.
/// </summary>
public class IntegrityCheckpointWorkerCoverageTests {

  // Target: src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs:47 — `return;` in the
  // `catch (OperationCanceledException)` around `_schemaReadyGate.WaitForReadyAsync`. If this
  // regressed to letting the exception escape, a pod stopped while still waiting for migrations
  // would fault its BackgroundService instead of shutting down quietly, and every fast restart
  // during a rolling deploy would log a crash for something that is not one.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledWhileWaitingForSchemaReady_ReturnsQuietlyAsync(
      CancellationToken testToken) {
    var dispatcher = new _captureDispatcher();
    // A gate that never opens AND reports when a waiter arrives. A plain never-ready gate could not
    // say whether the worker had reached the barrier: StartAsync only SCHEDULES ExecuteAsync, so
    // StopAsync's cancellation can beat the body to the gate entirely and every assertion below
    // would be answered by a worker that never waited on anything.
    var gate = new _parkedGate();
    var worker = _buildWorker(new _checkpointCoordinator(), dispatcher, "origin-svc", gate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.Entered.WaitAsync(testToken);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a shutdown while still waiting for the schema must read as a clean exit, not a "
             + "crashed worker — a pod stopped mid-migration is a routine, not exceptional, event");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("nothing may be dispatched before the tables the checkpoint watermarks live in "
             + "have been created");
  }

  // Target: line 54 — `break;` in the `catch (OperationCanceledException)` around
  // `RunCheckpointOnceAsync` inside the main loop. If a spurious cancellation from a cycle were
  // treated like a generic failure (logged and retried) instead of ending the loop, a worker that
  // should have exited cleanly on shutdown would instead spin retrying a call whose token is
  // already dead.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CycleThrowsOperationCanceled_BreaksTheLoopWithoutRetryingAsync(
      CancellationToken testToken) {
    var coordinator = new _throwingCoordinator { ThrowOperationCanceled = true };
    var worker = _loopWorker(coordinator, SchemaReadyGate.AlreadyReady(),
      new StreamIntegrityOptions { CheckpointIntervalSeconds = 1 });

    await worker.StartAsync(testToken);
    await coordinator.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    var executeTask = worker.ExecuteTask;
    await executeTask!.WaitAsync(TimeSpan.FromSeconds(20), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse();
    await Assert.That(coordinator.Calls).IsEqualTo(1)
      .Because("an OperationCanceledException from the cycle itself must end the loop immediately "
             + "— retrying a call whose cancellation already fired can never succeed");
  }

  // Target: line 176 — `return 0;` when the origin has no bucket types AND no historically
  // audited types to fan out to, even though transport/serializer/catalog/routing are all wired.
  // Without this fallback an origin with nothing to checkpoint on its own event topics would
  // publish NOTHING at all, silencing the very liveness signal checkpoints exist to guarantee.
  [Test]
  public async Task RunCheckpointOnce_TransportWiredButNoTypesToFanOutTo_FallsBackToDispatcherAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 },
      // Deliberately empty: no bucket types, no historical own-audited types.
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, "origin-svc",
      transport: transport, catalog: new _catalog(ordersType));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("nothing to fan out to means the own-topics path found no candidates at all");
    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.Buckets).IsEmpty();
  }

  // Target: line 187 — `continue;` when a candidate type name does not resolve against the
  // catalog. A bad or stale event-type string must not abort the whole fan-out — every OTHER
  // resolvable topic still needs its heartbeat, or one unknown type silences every consumer.
  [Test]
  public async Task RunCheckpointOnce_UnresolvableEventType_IsSkippedButOthersStillPublishAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [
          new CheckpointBucket { TenantScope = null, EventType = "Bogus.Unresolvable.Type, NoSuchAssembly", Count = 1 },
        ],
      },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType)],
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, "origin-svc",
      transport: transport, catalog: new _catalog(ordersType));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the unresolvable bucket type must be skipped, not abort the fan-out — the known "
             + "type's topic still has to receive its checkpoint");
    await Assert.That(transport.Published[0].Destination.Address)
      .IsEqualTo("whizbang.core.tests.workers.checkpointtopicprobes.orders");
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  // Target: line 201 — `continue;   // not a registry-known event — no topic to ride.` on the
  // registry-routed path (no IOutboxRoutingStrategy). A type the catalog knows but the topic
  // registry does not map must be skipped rather than crash or silently drop every other topic.
  [Test]
  public async Task RunCheckpointOnce_RegistryRoutedHost_SkipsTypesWithNoRegistryTopicAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var usersType = typeof(CheckpointTopicProbes.Users.UsersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 9, ToCommitSequence = 9 },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType), TypeNameFormatter.Format(usersType)],
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    // Registry only knows the orders topic — users resolves via the catalog but has no mapped topic.
    var worker = _buildWorker(coordinator, dispatcher, "origin-svc",
      transport: transport, catalog: new _catalog(ordersType, usersType),
      outboxRouting: false, topicRegistry: new _topicRegistry((ordersType, "app.orders")));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Select(p => p.Destination.Address))
      .IsEquivalentTo(["app.orders"])
      .Because("a catalog-known type with no registry topic must be skipped, not stop the orders "
             + "topic (which IS mapped) from getting its checkpoint");
    await Assert.That(dispatcher.Published).IsEmpty();
  }

  // Target: line 208 — `return 0;` when every candidate type failed to resolve to a destination
  // (registry has entries, but nothing in this cycle could ride them). The dispatcher fallback is
  // what keeps the checkpoint from vanishing when the own-topics path comes up completely empty.
  [Test]
  public async Task RunCheckpointOnce_NoCandidateResolvesToADestination_FallsBackToDispatcherAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [
          new CheckpointBucket { TenantScope = null, EventType = "Bogus.Unresolvable.Type, NoSuchAssembly", Count = 1 },
        ],
      },
    };
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, dispatcher, "origin-svc",
      transport: transport, catalog: new _catalog(typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent)));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("the only candidate type was unresolvable, so no destination could ever be built");
    var checkpoint = (IntegrityCheckpoint)dispatcher.Published.Single();
    await Assert.That(checkpoint.FromCommitSequence).IsEqualTo(5L)
      .Because("a fan-out that resolves to nothing must still fall back to the dispatcher — "
             + "otherwise the checkpoint silently vanishes instead of heartbeating at all");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityCheckpointWorker _buildWorker(
      _checkpointCoordinator coordinator, _captureDispatcher dispatcher, string serviceName,
      _captureTransport? transport = null, IMessageTypeCatalog? catalog = null,
      bool outboxRouting = true, ITopicRegistry? topicRegistry = null,
      ISchemaReadyGate? gate = null) {
    var services = new ServiceCollection();
    services.AddSingleton<ICheckpointMint>(new CheckpointMint(Options.Create(new ControlClassOptions())));
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider(serviceName));
    if (transport is not null) {
      services.AddSingleton<ITransport>(transport);
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
      if (outboxRouting) {
        services.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
      }
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
      gate ?? new SchemaReadyGate(),
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  // Target: src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs:47 — the `return;` inside the
  // `catch (OperationCanceledException)` around the schema-gate wait.
  //
  // The older test above cancels via StopAsync without ever confirming the worker had reached the
  // gate, so it settles whether or not the wait was entered — and measurement showed line 47 never
  // running under it. This one blocks in the gate and publishes a signal from inside
  // WaitForReadyAsync, so the cancellation provably lands ON the wait.
  //
  // What breaks if the catch regresses: this worker reads and writes wh_integrity_* tables that do
  // not exist until migrations finish. A pod stopped mid-migration would fault its BackgroundService
  // (the generic host reports that as a startup failure) instead of exiting quietly, so every fast
  // restart in a rolling deploy would log a crash for a routine stop. The second assertion is the
  // load-bearing half: the worker must return, not fall through into the checkpoint loop against a
  // schema that is not there.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledWhileParkedOnTheSchemaGate_ReturnsBeforeAnyCheckpointCycleAsync(
      CancellationToken testToken) {
    var coordinator = new _throwingCoordinator();
    var gate = new _parkedGate();
    var worker = _loopWorker(coordinator, gate, new StreamIntegrityOptions { CheckpointIntervalSeconds = 1 });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Without this the cancellation could beat the worker to the gate and the assertions below
    // would be answered by a worker that never waited on anything.
    await gate.Entered.WaitAsync(testToken);
    var executeTask = worker.ExecuteTask!;

    await cts.CancelAsync();
    // A task exiting through a cancellation catch settles RanToCompletion or Canceled depending on
    // thread-pool timing, so suppress and assert completion rather than success.
    await executeTask.WaitAsync(TimeSpan.FromSeconds(20), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a shutdown arriving while the worker waits for migrations is an ordinary stop; "
             + "faulting here reports a crash for something that is not one");
    await Assert.That(coordinator.Calls).IsEqualTo(0)
      .Because("the worker must RETURN at the gate — running even one checkpoint cycle would issue "
             + "SQL against wh_integrity_* tables the migrations had not yet created");
  }

  /// <summary>A gate that never opens and announces the moment a worker begins waiting on it.</summary>
  private sealed class _parkedGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

  private static IntegrityCheckpointWorker _loopWorker(
      _throwingCoordinator coordinator, ISchemaReadyGate gate, StreamIntegrityOptions options) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(new _captureDispatcher());
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("origin-svc"));
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(options),
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

  /// <summary>Counts cycles and can fail the first one with a chosen exception shape.</summary>
  private sealed class _throwingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public bool ThrowOperationCanceled { get; init; }
    public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(TrackedGuid.NewMedo().Value);

    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      FirstCall.TrySetResult();
      if (ThrowOperationCanceled) {
        // A synthetic cancellation NOT tied to stoppingToken — proves the loop breaks on ANY
        // OperationCanceledException from the cycle, not only a real shutdown signal.
        throw new OperationCanceledException("simulated cycle-level cancellation");
      }
      return Task.FromResult<IntegrityCheckpointWindow?>(null);
    }
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
