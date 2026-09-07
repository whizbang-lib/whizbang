using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgDurableSignalTailWorker"/> paths the existing
/// <c>PgDurableSignalTailIntegrationTests</c> never exercise: the tick's early-out when no
/// signal types are registered, cancellation landing mid-flight during cursor initialization
/// and during a tick (as opposed to during the between-tick delay, which every existing test
/// already races), and the per-dispatch try/catch inside a tick -- both the awaited-pending
/// branch and the logged-and-swallowed exception branch.
/// </summary>
[Category("Shard1")]
public class PgDurableSignalTailWorkerCoverageTests : EFCoreTestBase {
  private readonly record struct DummyProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct PendingDeliveryProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct ThrowingDispatchProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct OkDispatchProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records the wire-name of the first Warning-level, EventId=4 (LogDispatchThrew) log
  /// call -- the deterministic signal this file uses to know a dispatch threw and was logged,
  /// instead of sleeping and hoping.</summary>
  private sealed class RecordingLogger : ILogger<PgDurableSignalTailWorker> {
    public TaskCompletionSource<string> DispatchThrew { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      if (eventId.Id == 4) {
        DispatchThrew.TrySetResult(formatter(state, exception));
      }
    }
  }

  private PgDurableSignalTailWorker _createTail(Guid instanceId, ISignalSink sink, ILogger<PgDurableSignalTailWorker>? logger = null) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1);
    return new PgDurableSignalTailWorker(
      Options.Create(opts), cfg, instance, sink,
      logger ?? NullLogger<PgDurableSignalTailWorker>.Instance);
  }

  private (PostgresSignalTransport Transport, IServiceInstanceProvider Instance) _createTransport(Guid instanceId) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1);
    var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    return (transport, instance);
  }

  /// <summary>Polls for the tail's own cursor row so a caller knows cursor initialization has
  /// landed and the tick loop has started, without sleeping blindly.</summary>
  private async Task _waitForCursorRowAsync(Guid instanceId, CancellationToken ct) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (DateTimeOffset.UtcNow < deadline) {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(ct);
      await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_signal_cursors WHERE instance_id = @id", conn);
      cmd.Parameters.AddWithValue("id", instanceId);
      var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
      if (count > 0) { return; }
      await Task.Delay(100, ct);
    }
    throw new TimeoutException($"Timed out waiting for a wh_signal_cursors row for instance {instanceId}.");
  }

  /// <summary>Polls real Postgres lock state (not a blind sleep) for a backend genuinely waiting
  /// on the ACCESS EXCLUSIVE lock a test is holding over <c>wh_signal_cursors</c> -- proof the
  /// worker's own statement is in flight and blocked, so a subsequent cancel lands mid-operation.</summary>
  private async Task _waitUntilBlockedOnCursorsLockAsync(CancellationToken ct) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (DateTimeOffset.UtcNow < deadline) {
      await using var probe = new NpgsqlConnection(ConnectionString);
      await probe.OpenAsync(ct);
      await using var cmd = new NpgsqlCommand(
        "SELECT COUNT(*) FROM pg_locks WHERE relation = 'wh_signal_cursors'::regclass AND granted = false", probe);
      var blocked = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
      if (blocked > 0) { return; }
      await Task.Delay(50, ct);
    }
    throw new TimeoutException("Timed out waiting for a waiter blocked on the wh_signal_cursors lock.");
  }

  private static async Task _waitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct) {
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    while (DateTimeOffset.UtcNow < deadline) {
      if (condition()) { return; }
      await Task.Delay(100, ct);
    }
    throw new TimeoutException("Timed out waiting for the expected condition.");
  }

  // ============================================================
  // Line 117: empty wire map short-circuits a tick before it ever opens a connection
  // ============================================================

  [Test]
  public async Task TickOnce_WithNoRegisteredSignalTypes_ReturnsWithoutTouchingTheConnectionPlanAsync() {
    // If this early return ever stopped firing, a pod with no durable signal handlers registered
    // would either burn a fresh connection every 2 seconds forever for nothing to dispatch, or --
    // as proven here by handing it no connection plan at all -- NullReferenceException on the very
    // first tick.
    var worker = _createTail(Guid.CreateVersion7(), new CountingSink());

    var field = typeof(PgDurableSignalTailWorker).GetField("_wireNameToEntry", BindingFlags.NonPublic | BindingFlags.Instance);
    await Assert.That(field).IsNotNull()
      .Because("this test targets PgDurableSignalTailWorker's private wire map field by exact name");
    field!.SetValue(worker, new Dictionary<string, SignalTypeEntry>(StringComparer.Ordinal));

    var method = typeof(PgDurableSignalTailWorker).GetMethod("_tickOnceAsync", BindingFlags.NonPublic | BindingFlags.Instance);
    await Assert.That(method).IsNotNull()
      .Because("this test targets PgDurableSignalTailWorker's private tick method by exact name");

    var task = (Task)method!.Invoke(worker, [null, CancellationToken.None])!;
    await task;

    await Assert.That(task.IsCompletedSuccessfully).IsTrue()
      .Because("an empty wire map must return immediately without dereferencing the (here, absent) connection plan");
  }

  // ============================================================
  // Lines 82 & 91: cancellation landing mid-flight, not just during the between-tick delay
  // ============================================================
  //
  // Every existing cancellation test races the CancellationTokenSource against
  // Task.Delay(_tickInterval, stoppingToken) between ticks -- that always lands in the OTHER
  // OperationCanceledException catch (the one around the delay). These two tests instead hold a
  // real ACCESS EXCLUSIVE lock on wh_signal_cursors so the worker's own statement is genuinely
  // blocked in Postgres, then cancel while it is waiting -- landing the cancellation inside the
  // statement itself, which is the only way to reach the catch around _initializeCursorAsync /
  // _tickOnceAsync rather than the one around the delay.

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CursorInitCanceledWhileBlockedOnTheCursorsTable_ReturnsWithoutFaultingAsync(
      CancellationToken testToken) {
    // Cursor initialization is the very first statement the tail issues. If its own cancellation
    // ever escaped this specific catch as a plain Exception, an ordinary shutdown landing exactly
    // here would be logged as an init failure and the loop would start ticking anyway against a
    // connection the host already asked to cancel, instead of the worker ending cleanly like
    // every other pre-loop cancellation path.
    var worker = _createTail(Guid.CreateVersion7(), new CountingSink());

    await using var lockConn = new NpgsqlConnection(ConnectionString);
    await lockConn.OpenAsync(testToken);
    await using var lockTx = await lockConn.BeginTransactionAsync(testToken);
    await using (var lockCmd = new NpgsqlCommand("LOCK TABLE wh_signal_cursors IN ACCESS EXCLUSIVE MODE", lockConn, lockTx)) {
      await lockCmd.ExecuteNonQueryAsync(testToken);
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    try {
      await worker.StartAsync(cts.Token);
      await _waitUntilBlockedOnCursorsLockAsync(testToken);
      await cts.CancelAsync();

      var executeTask = worker.ExecuteTask;
      await executeTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

      await Assert.That(executeTask.IsCompleted).IsTrue()
        .Because("cancellation during cursor init must end ExecuteAsync, not hang it");
      await Assert.That(executeTask.IsFaulted).IsFalse()
        .Because("a canceled cursor-init must end the worker cleanly instead of surfacing as a fault");
    } finally {
      await lockTx.RollbackAsync(CancellationToken.None);
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_TickCanceledWhileBlockedOnTheCursorsTable_BreaksTheLoopWithoutFaultingAsync(
      CancellationToken testToken) {
    // Every tick reads wh_signal_cursors. If a cancellation landing mid-query here ever escaped
    // as a general Exception instead of this specific catch, host shutdown arriving during a tick
    // would be logged as a tick failure (and the loop would only stop moments later, by accident,
    // when the between-tick delay observes the same canceled token) instead of the tick itself
    // recognizing the shutdown and ending the loop immediately.
    const string wireName = "utest-tickcancel-dummy-71042";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(DummyProbe), wireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<DummyProbe>(default, ct)),
    ]));

    var instanceId = Guid.CreateVersion7();
    var worker = _createTail(instanceId, new CountingSink());

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    NpgsqlConnection? lockConn = null;
    NpgsqlTransaction? lockTx = null;
    try {
      await worker.StartAsync(cts.Token);

      // Wait for cursor init to land before taking the lock, so the lock only ever blocks the
      // loop's own SELECT, never the init INSERT that has to succeed first.
      await _waitForCursorRowAsync(instanceId, testToken);

      lockConn = new NpgsqlConnection(ConnectionString);
      await lockConn.OpenAsync(testToken);
      lockTx = await lockConn.BeginTransactionAsync(testToken);
      await using (var lockCmd = new NpgsqlCommand("LOCK TABLE wh_signal_cursors IN ACCESS EXCLUSIVE MODE", lockConn, lockTx)) {
        await lockCmd.ExecuteNonQueryAsync(testToken);
      }

      await _waitUntilBlockedOnCursorsLockAsync(testToken);
      await cts.CancelAsync();

      var executeTask = worker.ExecuteTask;
      await executeTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

      await Assert.That(executeTask.IsCompleted).IsTrue()
        .Because("cancellation during a tick must end ExecuteAsync, not hang it");
      await Assert.That(executeTask.IsFaulted).IsFalse()
        .Because("a mid-tick cancellation must break the loop cleanly instead of surfacing as a fault");
    } finally {
      if (lockTx is not null) { await lockTx.RollbackAsync(CancellationToken.None); }
      if (lockConn is not null) { await lockConn.DisposeAsync(); }
      await worker.StopAsync(CancellationToken.None);
    }
  }

  // ============================================================
  // Lines 155 & 156: a dispatch that does not complete synchronously is awaited to completion
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task TickOnce_DispatchNotSynchronouslyComplete_AwaitsAndDeliversTheSignalAsync(CancellationToken testToken) {
    // If a dispatch whose handler yields before delivering were fired-and-forgotten instead of
    // awaited here, the cursor could advance past the signal before its delivery actually
    // finished. A pod restart landing in that gap would never redeliver it -- the tail only
    // replays rows past the cursor -- so the signal would be gone: no delivery, no error, no trace.
    const string wireName = "utest-tick-pending-56213";
    var sink = new CountingSink();
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(PendingDeliveryProbe), wireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static async (s, ct) => {
          await Task.Yield();
          await s.ReceiveAsync<PendingDeliveryProbe>(default, ct);
        }),
    ]));

    var (transport, _) = _createTransport(Guid.NewGuid());
    await transport.StartAsync(new CountingSink(), testToken);

    var tailInstanceId = Guid.NewGuid();
    var tail = _createTail(tailInstanceId, sink);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await tail.StartAsync(cts.Token);

    try {
      await _waitForCursorRowAsync(tailInstanceId, testToken);
      await transport.PublishAsync(new PendingDeliveryProbe(1), SignalTarget.Broadcast, testToken);
      await _waitUntilAsync(() => sink.Received > 0, TimeSpan.FromSeconds(15), testToken);

      await Assert.That(sink.Received).IsEqualTo(1)
        .Because("the tick must await a non-synchronously-completed dispatch to completion before "
               + "moving on, or a yielding handler's delivery could be lost entirely");
    } finally {
      await tail.StopAsync(CancellationToken.None);
    }
  }

  // ============================================================
  // Lines 158 & 159: one throwing dispatch is logged and does not stop the others
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task TickOnce_DispatchThrows_LogsAndOtherDispatchesInTheSameTickStillDeliverAsync(CancellationToken testToken) {
    // A single misbehaving handler must not take down the tail or block its siblings: if this
    // catch regressed, one throwing receptor would either crash the worker -- turning a handler
    // bug into a stalled durable-signal backstop for every type it carries -- or leave the
    // exception unlogged, so an operator would see delivery silently stop with no record of why.
    const string throwingWireName = "utest-tick-throw-88304";
    const string okWireName = "utest-tick-throw-ok-88305";
    var sink = new CountingSink();
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(ThrowingDispatchProbe), throwingWireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (s, ct) => throw new InvalidOperationException("utest dispatch failure")),
      new SignalTypeEntry(typeof(OkDispatchProbe), okWireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (s, ct) => s.ReceiveAsync<OkDispatchProbe>(default, ct)),
    ]));

    var recordingLogger = new RecordingLogger();
    var (transport, _) = _createTransport(Guid.NewGuid());
    await transport.StartAsync(new CountingSink(), testToken);

    var tailInstanceId = Guid.NewGuid();
    var tail = _createTail(tailInstanceId, sink, recordingLogger);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await tail.StartAsync(cts.Token);

    try {
      await _waitForCursorRowAsync(tailInstanceId, testToken);
      await transport.PublishAsync(new ThrowingDispatchProbe(1), SignalTarget.Broadcast, testToken);
      await transport.PublishAsync(new OkDispatchProbe(1), SignalTarget.Broadcast, testToken);

      var loggedMessage = await recordingLogger.DispatchThrew.Task.WaitAsync(TimeSpan.FromSeconds(15), testToken);
      await _waitUntilAsync(() => sink.Received > 0, TimeSpan.FromSeconds(15), testToken);

      await Assert.That(sink.Received).IsEqualTo(1)
        .Because("a throwing dispatch must not prevent another signal from the same publish batch "
               + "from being delivered");
      await Assert.That(loggedMessage).Contains(throwingWireName)
        .Because("the log must identify which wire-name's dispatch failed, or an operator cannot "
               + "tell which signal type stopped delivering");

      var executeTask = tail.ExecuteTask;
      await Assert.That(executeTask!.IsFaulted).IsFalse()
        .Because("one handler's exception must not fault the tail worker itself");
    } finally {
      await tail.StopAsync(CancellationToken.None);
    }
  }
}
