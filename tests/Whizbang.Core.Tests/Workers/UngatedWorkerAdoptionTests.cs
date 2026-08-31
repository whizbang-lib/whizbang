using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Increment 3's remaining barriers: the formerly-ungated background services declare what they
/// actually depend on — one at a time, each with the same shape. A worker whose work touches the
/// database (or lets a broker deliver into it) waits for the schema gate before beginning, and a
/// worker constructed without a gate (every existing test fixture) behaves exactly as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportDeadLetterDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveMigrationWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/BackupTickCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 102)]
public class UngatedWorkerAdoptionTests {

  private sealed class _countingScopeFactory : IServiceScopeFactory {
    private readonly IServiceScopeFactory _inner;
    private int _count;
    public _countingScopeFactory(IServiceScopeFactory inner) { _inner = inner; }
    public int Count => Volatile.Read(ref _count);
    public IServiceScope CreateScope() {
      Interlocked.Increment(ref _count);
      return _inner.CreateScope();
    }
  }

  private static async Task _assertGatedAsync(
      Func<int> observed, Func<Task> start, SchemaReadyGate gate, string because) {
    await start();
    await Task.Delay(300);
    await Assert.That(observed()).IsEqualTo(0).Because(because);

    gate.MarkReady();
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (observed() == 0 && DateTime.UtcNow < deadline) {
      await Task.Delay(10);
    }
    await Assert.That(observed()).IsGreaterThan(0)
      .Because("once migrations complete the work must actually run — waiting is not skipping");
  }

  // ── TransportDeadLetterDrainWorker ──────────────────────────────────────

  [Test]
  public async Task DeadLetterDrain_DoesNotDrainUntilTheGateOpensAsync() {
    var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new _countingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());
    var gate = new SchemaReadyGate();
    var worker = new TransportDeadLetterDrainWorker(
      scopeFactory,
      Options.Create(new TransportDeadLetterDrainWorkerOptions { IntervalMinutes = 1 }),
      new WhizbangMetrics(),
      NullLogger<TransportDeadLetterDrainWorker>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => scopeFactory.Count,
      () => worker.StartAsync(cts.Token),
      gate,
      "draining writes to wh_dead_letters, which may not exist on a first boot");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── PerspectiveMigrationWorker ──────────────────────────────────────────

  [Test]
  public async Task PerspectiveMigration_DoesNotQueryPendingRebuildsUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var calls = 0;
    var worker = new PerspectiveMigrationWorker(
      new _noOpRebuilder(),
      NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: gate) {
      GetPendingRebuilds = _ => {
        Interlocked.Increment(ref calls);
        return Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([]);
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask,
    };

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => Volatile.Read(ref calls),
      () => worker.StartAsync(cts.Token),
      gate,
      "pending-rebuild queries are database work");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── BackupTickCoordinator ───────────────────────────────────────────────

  [Test]
  public async Task BackupTickCoordinator_DoesNotStartItsLoopUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var tracker = new _countingTracker();
    var worker = new BackupTickCoordinator(
      tracker,
      new BackupTickRegistry(),
      Options.Create(new BackupTickCoordinatorOptions()),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => tracker.Reads,
      () => worker.StartAsync(cts.Token),
      gate,
      "registered backstop ticks poll the database — the coordinator must not begin before it exists");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── ServiceBusConsumerWorker ────────────────────────────────────────────

  [Test]
  public async Task ServiceBusConsumer_DoesNotSubscribeUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new ServiceBusConsumerWorker(
      transport: new Whizbang.Core.Transports.InProcessTransport(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: JsonContextRegistry.CreateCombinedOptions(),
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] },
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(300);

    await Assert.That(worker.SubscriptionsReady.IsCompleted).IsFalse()
      .Because("subscribing lets the broker deliver, and delivery lands in inbox tables the "
             + "migration creates — nothing may be subscribed before the gate opens");

    gate.MarkReady();
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── TransportConsumerWorker ─────────────────────────────────────────────

  [Test]
  public async Task TransportConsumer_DoesNotSubscribeUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    using var sp = new ServiceCollection().BuildServiceProvider();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new Whizbang.Core.Transports.TransportDestination("dest-a"));
    var worker = new TransportConsumerWorker(
      transport: new Whizbang.Core.Transports.InProcessTransport(),
      options: options,
      resilienceOptions: new Whizbang.Core.Resilience.SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: JsonContextRegistry.CreateCombinedOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      schemaReadyGate: gate,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(300);

    await Assert.That(worker.SubscriptionsReady.IsCompleted).IsFalse()
      .Because("subscribing lets the broker deliver before the schema exists — the existing "
             + "SubscriptionsReady signal must not fire until the gate opens");

    gate.MarkReady();
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  private sealed class _noOpRebuilder : IPerspectiveRebuilder {
    private static Task<RebuildResult> _empty(string name) =>
      Task.FromResult(new RebuildResult(name, 0, 0, TimeSpan.Zero, true, null));
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => _empty(perspectiveName);
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class _countingTracker : IIdleActivityTracker {
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public TimeSpan TimeSinceLastActivity {
      get { Interlocked.Increment(ref _reads); return TimeSpan.Zero; }
    }
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => "test";
    public void Touch(string source) { }
  }

}
