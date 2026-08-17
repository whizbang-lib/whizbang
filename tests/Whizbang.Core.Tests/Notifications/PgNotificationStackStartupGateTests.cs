using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Increment 3, the Pg notification stack: the four table-touching notification workers wait for
/// the schema gate before beginning. <c>PgDurableSignalTailWorker</c> INSERTs its cursor into
/// <c>wh_signal_cursors</c> the moment it starts; <c>PgInstanceLifecycleMonitor</c> reads
/// <c>wh_service_instances</c> on its first tick; <c>PgDurableSignalRetentionWorker</c> DELETEs
/// from <c>wh_signals</c>; <c>PgCommitOrderStamperWorker</c> calls the
/// <c>stamp_pending_commit_sequences</c> function. None of those objects exist on a first boot
/// until the migration completes.
///
/// <para><c>PgSharedNotifyConnection</c> and <c>PgWorkNotificationListener</c> are deliberately
/// NOT gated: LISTEN/NOTIFY and the session advisory alive-lock need no schema, and the shared
/// connection is the liveness substrate (its <c>application_name</c> is what
/// <c>wh_live_instances</c> joins against) — later pipeline stages that run BEFORE Migrate need
/// it up.</para>
///
/// <para>The observable: every gated worker's first act is resolving its connection string, which
/// consults <see cref="IConfiguration"/>. A counting configuration therefore sees zero reads while
/// the gate is closed and &gt;0 once it opens — no real Postgres needed.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalTailWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 103)]
public class PgNotificationStackStartupGateTests {

  private static WhizbangNotificationOptions _optionsWithKey() =>
    new() { ConnectionStringKey = "db" };

  private static IConfiguration _plainConfig() =>
    new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private static async Task _assertGatedAsync(
      Func<int> observed, Func<Task> start, SchemaReadyGate gate, string because) {
    await start();
    await Task.Delay(400);
    await Assert.That(observed()).IsEqualTo(0).Because(because);

    gate.MarkReady();
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (observed() == 0 && DateTime.UtcNow < deadline) {
      await Task.Delay(10);
    }
    await Assert.That(observed()).IsGreaterThan(0)
      .Because("once migrations complete the work must actually run — waiting is not skipping");
  }

  // ── PgDurableSignalTailWorker ───────────────────────────────────────────

  [Test]
  public async Task DurableSignalTail_DoesNotInitializeItsCursorUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var config = new _countingConfiguration();
    var worker = new PgDurableSignalTailWorker(
      Options.Create(_optionsWithKey()),
      config,
      new ServiceInstanceProvider(_plainConfig()),
      new _noOpSink(),
      NullLogger<PgDurableSignalTailWorker>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => config.Reads,
      () => worker.StartAsync(cts.Token),
      gate,
      "the tail's first act is INSERTing this pod's cursor into wh_signal_cursors, "
      + "which does not exist before the migration runs");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── PgInstanceLifecycleMonitor ──────────────────────────────────────────

  [Test]
  public async Task InstanceLifecycleMonitor_DoesNotScanForDeathsUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var config = new _countingConfiguration();
    var worker = new PgInstanceLifecycleMonitor(
      Options.Create(_optionsWithKey()),
      config,
      new _noOpSignalBus(),
      NullLogger<PgInstanceLifecycleMonitor>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => config.Reads,
      () => worker.StartAsync(cts.Token),
      gate,
      "the monitor's first tick reads wh_service_instances — and a death it announced "
      + "against a half-migrated fleet table would trigger takeover from garbage data");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── PgDurableSignalRetentionWorker ──────────────────────────────────────

  [Test]
  public async Task DurableSignalRetention_DoesNotSweepUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var config = new _countingConfiguration();
    var worker = new PgDurableSignalRetentionWorker(
      Options.Create(_optionsWithKey()),
      config,
      NullLogger<PgDurableSignalRetentionWorker>.Instance,
      schemaReadyGate: gate) {
      SweepInterval = TimeSpan.FromMilliseconds(100),
    };

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => config.Reads,
      () => worker.StartAsync(cts.Token),
      gate,
      "the retention sweep DELETEs from wh_signals; the gate must come before the "
      + "interval delay so a slow migration is still never raced");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── PgCommitOrderStamperWorker ──────────────────────────────────────────

  [Test]
  public async Task CommitOrderStamper_DoesNotEnterElectionUntilTheGateOpensAsync() {
    var gate = new SchemaReadyGate();
    var config = new _countingConfiguration();
    var worker = new PgCommitOrderStamperWorker(
      Options.Create(_optionsWithKey()),
      Options.Create(new CommitOrderStamperOptions()),
      config,
      new _fakeSharedConnection(),
      NullLogger<PgCommitOrderStamperWorker>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await _assertGatedAsync(
      () => config.Reads,
      () => worker.StartAsync(cts.Token),
      gate,
      "the stamper's leader loop calls stamp_pending_commit_sequences — a function the "
      + "migration defines");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  /// <summary>Counts configuration reads — the gated workers' first act is resolving their
  /// connection string, which consults configuration, so zero reads means none began.</summary>
  private sealed class _countingConfiguration : IConfiguration {
    private readonly IConfiguration _inner = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    private int _reads;
    public int Reads => Volatile.Read(ref _reads);
    public string? this[string key] {
      get { Interlocked.Increment(ref _reads); return _inner[key]; }
      set => _inner[key] = value;
    }
    public IEnumerable<IConfigurationSection> GetChildren() {
      Interlocked.Increment(ref _reads);
      return _inner.GetChildren();
    }
    public IChangeToken GetReloadToken() => _inner.GetReloadToken();
    public IConfigurationSection GetSection(string key) {
      Interlocked.Increment(ref _reads);
      return _inner.GetSection(key);
    }
  }

  private sealed class _noOpSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  private sealed class _noOpSignalBus : ISignalBus {
    private sealed class _subscription : ISignalSubscription {
      public void Dispose() { }
    }
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
      where TSignal : ISignal => new _subscription();
  }

  private sealed class _fakeSharedConnection : ISharedNotifyConnection {
    private sealed class _handle : IDisposable {
      public void Dispose() { }
    }
    public IDisposable Subscribe(INotifySubscription subscription) => new _handle();
  }
}
