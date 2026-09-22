using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Increment 3's remaining barriers: the formerly-ungated background services declare what they
/// actually depend on — one at a time, each with the same shape. A worker whose work touches the
/// database (or lets a broker deliver into it) waits for the schema gate before beginning, and a
/// worker constructed without a gate (every existing test fixture) behaves exactly as before.
/// </summary>
/// <remarks>
/// Every test here proves "nothing before the gate" without sleeping: the gate double reports when
/// a waiter arrives, so the assertion runs at the moment the worker is provably parked on the gate,
/// and the "work happens after" half waits on the work's own signal. A fixed delay proved the
/// negative only on an idle machine and failed under a coverage-instrumented parallel run.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportDeadLetterDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveMigrationWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/BackupTickCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 102)]
public class UngatedWorkerAdoptionTests {
  private static readonly TimeSpan _safetyNet = TimeSpan.FromSeconds(30);

  /// <summary>
  /// A schema gate that says when a waiter has arrived. That is the deterministic moment to assert
  /// "no work yet": the worker is parked on <see cref="WaitForReadyAsync"/> and cannot proceed until
  /// <see cref="MarkReady"/>, so anything it did before that point has already been counted.
  /// </summary>
  private sealed class ObservableGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _waiterArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaiterArrived => _waiterArrived.Task;
    public bool IsReady => _ready.Task.IsCompleted;

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waiterArrived.TrySetResult();
      return _ready.Task.WaitAsync(cancellationToken);
    }

    public void MarkReady() => _ready.TrySetResult();
  }

  private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory {
    private readonly IServiceScopeFactory _inner = inner;
    private readonly TaskCompletionSource _firstUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public int Count => Volatile.Read(ref _count);
    public Task FirstUse => _firstUse.Task;
    public IServiceScope CreateScope() {
      Interlocked.Increment(ref _count);
      _firstUse.TrySetResult();
      return _inner.CreateScope();
    }
  }

  private static async Task _assertGatedAsync(
      Func<int> observed, Task firstObservation, Func<Task> start, ObservableGate gate, string because) {
    await start();

    // The worker is now parked on the gate — provably, not probably.
    await gate.WaiterArrived.WaitAsync(_safetyNet);
    await Assert.That(observed()).IsEqualTo(0).Because(because);

    gate.MarkReady();
    await firstObservation.WaitAsync(_safetyNet);
    await Assert.That(observed()).IsGreaterThan(0)
      .Because("once migrations complete the work must actually run — waiting is not skipping");
  }

  // ── TransportDeadLetterDrainWorker ──────────────────────────────────────

  [Test]
  public async Task DeadLetterDrain_DoesNotDrainUntilTheGateOpensAsync() {
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());
    var gate = new ObservableGate();
    var worker = new TransportDeadLetterDrainWorker(
      scopeFactory,
      Options.Create(new TransportDeadLetterDrainWorkerOptions { IntervalMinutes = 1 }),
      new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()),
      NullLogger<TransportDeadLetterDrainWorker>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => scopeFactory.Count,
      scopeFactory.FirstUse,
      () => worker.StartAsync(cts.Token),
      gate,
      "draining writes to wh_dead_letters, which may not exist on a first boot");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── PerspectiveMigrationWorker ──────────────────────────────────────────

  [Test]
  public async Task PerspectiveMigration_DoesNotQueryPendingRebuildsUntilTheGateOpensAsync() {
    var gate = new ObservableGate();
    var calls = 0;
    var firstQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new PerspectiveMigrationWorker(
      new NoOpRebuilder(),
      NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: gate) {
      GetPendingRebuilds = _ => {
        Interlocked.Increment(ref calls);
        firstQuery.TrySetResult();
        return Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([]);
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask,
    };

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => Volatile.Read(ref calls),
      firstQuery.Task,
      () => worker.StartAsync(cts.Token),
      gate,
      "pending-rebuild queries are database work");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── BackupTickCoordinator ───────────────────────────────────────────────

  [Test]
  public async Task BackupTickCoordinator_DoesNotStartItsLoopUntilTheGateOpensAsync() {
    var gate = new ObservableGate();
    var tracker = new CountingTracker();
    var worker = new BackupTickCoordinator(
      tracker: tracker,
      registry: new BackupTickRegistry(),
      options: Options.Create(new BackupTickCoordinatorOptions()),
      logger: NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: gate,
      gate: NullNotifySignalingGate.Instance);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => tracker.Reads,
      tracker.FirstRead,
      () => worker.StartAsync(cts.Token),
      gate,
      "the backup tick reads idle activity and drives registrars that touch the database");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── ServiceBusConsumerWorker ────────────────────────────────────────────

  [Test]
  public async Task ServiceBusConsumer_DoesNotSubscribeUntilTheGateOpensAsync() {
    var gate = new ObservableGate();
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new ServiceBusConsumerWorker(
      transport: new Whizbang.Core.Transports.InProcessTransport(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance),
      schemaReadyGate: gate,
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.WaiterArrived.WaitAsync(_safetyNet);

    await Assert.That(worker.SubscriptionsReady.IsCompleted).IsFalse()
      .Because("subscribing lets the broker deliver, and delivery lands in inbox tables the "
             + "migration creates — nothing may be subscribed before the gate opens");

    gate.MarkReady();
    await worker.SubscriptionsReady.WaitAsync(_safetyNet);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── TransportConsumerWorker ─────────────────────────────────────────────

  [Test]
  public async Task TransportConsumer_DoesNotSubscribeUntilTheGateOpensAsync() {
    var gate = new ObservableGate();
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new Whizbang.Core.Transports.TransportDestination("dest-a"));
    var worker = new TransportConsumerWorker(
      transport: new Whizbang.Core.Transports.InProcessTransport(),
      options: options,
      resilienceOptions: new Whizbang.Core.Resilience.SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: JsonContextRegistry.CreateCombinedOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: gate,
      routingOptions: Options.Create(new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.WaiterArrived.WaitAsync(_safetyNet);

    await Assert.That(worker.SubscriptionsReady.IsCompleted).IsFalse()
      .Because("subscribing lets the broker deliver before the schema exists — the existing "
             + "SubscriptionsReady signal must not fire until the gate opens");

    gate.MarkReady();
    await worker.SubscriptionsReady.WaitAsync(_safetyNet);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  private sealed class NoOpRebuilder : IPerspectiveRebuilder {
    private static Task<RebuildResult> _empty(string name) =>
      Task.FromResult(new RebuildResult(name, 0, 0, TimeSpan.Zero, true, null));
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class CountingTracker : IIdleActivityTracker {
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public Task FirstRead => _firstRead.Task;
    public TimeSpan TimeSinceLastActivity {
      get {
        Interlocked.Increment(ref _reads);
        _firstRead.TrySetResult();
        return TimeSpan.Zero;
      }
    }
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => "test";
    public void Touch(string source) { }
  }
}
