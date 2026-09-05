using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
  [Test]
  [Timeout(60000)]
  public async Task InstanceLifecycleMonitor_AnUnreachableDatabase_DoesNotStopDeathDetectionAsync(
      CancellationToken ct) {
    // This loop is what announces a dead pod, and the announcement is what triggers takeover of
    // the streams that pod owned. If a failed tick ended the loop, no death would be announced
    // again for the life of the process and those streams would stay stranded -- with nothing in
    // the logs after the first warning to say the monitor had stopped watching.
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var log = new _eventCountingLogger<PgInstanceLifecycleMonitor>(TICK_FAILED_EVENT_ID, target: 2);
    var worker = new PgInstanceLifecycleMonitor(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = UNREACHABLE_DATABASE }),
      _plainConfig(),
      new _noOpSignalBus(),
      log,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // The SECOND failure is the assertion: it can only happen if the loop survived the first and
    // came back round after its interval. The first proves only that a tick ran.
    await log.Reached.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the monitor keeps scanning through a database outage and still stops cleanly "
             + "when asked");
  }

  [Test]
  [Timeout(30000)]
  public async Task InstanceLifecycleMonitor_ShutdownBeforeTheGateOpens_ExitsCleanlyAsync(
      CancellationToken ct) {
    // A pod stopped while the migration is still running never gets to scan. That exit has to be
    // an ordinary shutdown: a fault here would report a death-detection crash on every rollout
    // that happens to be slow.
    var gate = new _blockingGate();
    var worker = new PgInstanceLifecycleMonitor(
      Options.Create(_optionsWithKey()),
      _plainConfig(),
      new _noOpSignalBus(),
      NullLogger<PgInstanceLifecycleMonitor>.Instance,
      schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Observed at the gate first, or "exited cleanly" is answered by a worker that never began.
    await gate.WaitEntered.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("stopping while gated is a shutdown, not a monitor failure");
  }

  /// <summary>A refused port: the connection plan is available, and opening it always fails.</summary>
  private const string UNREACHABLE_DATABASE =
    "Host=127.0.0.1;Port=1;Database=none;Username=u;Password=p;Timeout=1;Command Timeout=1";

  /// <summary>A schema gate that never opens, and reports when the worker began waiting on it.</summary>
  private sealed class _blockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waitEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitEntered => _waitEntered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task DurableSignalRetention_AnUnreachableDatabase_DoesNotStopTheSweepAsync(
      CancellationToken ct) {
    // The sweep is the only thing bounding wh_signals. Every durable signal ever published stays
    // in that table until this loop deletes it, so a loop that ends on one failed sweep leaves the
    // table growing without limit for the life of the process -- and the next sweep after a
    // restart has that much more to delete.
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var log = new _eventCountingLogger<PgDurableSignalRetentionWorker>(SWEEP_FAILED_EVENT_ID, target: 2);
    var worker = new PgDurableSignalRetentionWorker(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = UNREACHABLE_DATABASE }),
      _plainConfig(),
      log,
      schemaReadyGate: gate) {
      SweepInterval = TimeSpan.FromMilliseconds(50),
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await log.Reached.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the sweep retries through an outage and still stops cleanly when asked");
  }

  [Test]
  [Timeout(30000)]
  public async Task DurableSignalRetention_ShutdownBeforeTheGateOpens_ExitsCleanlyAsync(
      CancellationToken ct) {
    // The gate deliberately comes before the interval delay, so a host stopped during a slow
    // migration is still waiting here rather than sweeping. That exit must be an ordinary stop.
    var gate = new _blockingGate();
    var worker = new PgDurableSignalRetentionWorker(
      Options.Create(_optionsWithKey()),
      _plainConfig(),
      NullLogger<PgDurableSignalRetentionWorker>.Instance,
      schemaReadyGate: gate) {
      SweepInterval = TimeSpan.FromMilliseconds(50),
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.WaitEntered.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("stopping while gated is a shutdown, not a retention failure");
  }

  private const int TICK_FAILED_EVENT_ID = 1;
  private const int SWEEP_FAILED_EVENT_ID = 1;

  /// <summary>
  /// Completes a task once a given log event has been seen <c>target</c> times.
  /// </summary>
  /// <remarks>
  /// The second occurrence is what these tests wait on. The first only proves a pass ran; the
  /// second cannot happen unless the loop survived that pass and came back round after its
  /// interval, which is the whole claim.
  /// </remarks>
  private sealed class _eventCountingLogger<T>(int eventId, int target) : ILogger<T> {
    private readonly TaskCompletionSource _reached =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    public Task Reached => _reached.Task;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId id, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (id.Id == eventId && Interlocked.Increment(ref _seen) >= target) {
        _reached.TrySetResult();
      }
    }
  }

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
