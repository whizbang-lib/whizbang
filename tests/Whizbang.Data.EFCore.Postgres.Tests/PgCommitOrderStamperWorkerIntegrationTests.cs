// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26 step 3 — integration tests for <see cref="PgCommitOrderStamperWorker"/>.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>The worker acquires <c>pg_try_advisory_lock</c> on startup and
/// becomes the active stamper; <see cref="PgCommitOrderStamperWorker.IsLeader"/> = true.</description></item>
/// <item><description>Polling tick fires <c>stamp_pending_commit_sequences</c>; rows
/// get a non-NULL <c>commit_sequence</c> within bounded latency without any external
/// NOTIFY.</description></item>
/// <item><description>Only one of two concurrent workers becomes leader — the other
/// sleeps in the retry loop.</description></item>
/// <item><description>When the leader stops cleanly, the lock auto-releases (session
/// scope) and a contender's next retry tick acquires it.</description></item>
/// <item><description>Killswitch <see cref="CommitOrderStamperOptions.DisableStamper"/>
/// causes the worker to exit immediately without acquiring the lock.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
[Category("Shard3")]
public class PgCommitOrderStamperWorkerIntegrationTests : EFCoreTestBase {

  private PgCommitOrderStamperWorker _newWorker(
      string connectionString,
      TimeSpan? pollingInterval = null,
      TimeSpan? leaderElectionRetry = null,
      bool disable = false,
      INotifySignalingGate? gate = null,
      string? searchPath = null,
      int batchSize = 100,
      ILogger<PgCommitOrderStamperWorker>? logger = null,
      TimeProvider? timeProvider = null) {
    var notificationOptions = new WhizbangNotificationOptions {
      DirectConnectionString = connectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
      SearchPath = searchPath,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(100),
      LeaderElectionRetry = leaderElectionRetry ?? TimeSpan.FromMilliseconds(100),
      BatchSize = batchSize,
      DisableStamper = disable,
    };
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    // Slice 33.5 — use a real PgSharedNotifyConnection for the wh_committed LISTEN. Tests
    // that exercise the NOTIFY-fired wake path also need to start the shared conn so its
    // dispatch loop is running.
    var instanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(config);
    var shared = new PgSharedNotifyConnection(
      Options.Create(notificationOptions),
      config,
      instanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    return new PgCommitOrderStamperWorker(
      Options.Create(notificationOptions),
      Options.Create(stamperOptions),
      config,
      shared,
      logger ?? NullLogger<PgCommitOrderStamperWorker>.Instance,
      dutyElector: null,
      notifySignalingGate: gate,
      timeProvider: timeProvider);
  }

  /// <summary>
  /// A gate whose availability a test flips by hand, counting the worker's subscription so the
  /// test can prove the stamper listens while it runs and lets go when it stops.
  /// </summary>
  private sealed class FakeSignalingGate : INotifySignalingGate {
    private Action<bool>? _handlers;
    public bool IsAvailable { get; private set; } = true;
    public int Subscribers { get; private set; }
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged {
      add { _handlers += value; Subscribers++; }
      remove { _handlers -= value; Subscribers--; }
    }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
    public void Set(bool available) {
      IsAvailable = available;
      _handlers?.Invoke(available);
    }
  }

  private static Task<TaskCompletionSource> _whenBecomesLeaderAsync(PgCommitOrderStamperWorker worker) {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnBecameLeader += () => tcs.TrySetResult();
    return Task.FromResult(tcs);
  }

  private static TaskCompletionSource _whenStampCount(PgCommitOrderStamperWorker worker, int minStamped) {
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var total = 0;
    worker.OnStampCompleted += count => {
      var t = Interlocked.Add(ref total, count);
      if (t >= minStamped) {
        tcs.TrySetResult();
      }
    };
    return tcs;
  }

  [Test]
  public async Task Worker_StampsPendingRowsOnPollingTickAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var worker = _newWorker(ConnectionString);
    var leaderTcs = await _whenBecomesLeaderAsync(worker);
    var stampTcs = _whenStampCount(worker, minStamped: 1);
    await worker.StartAsync(cts.Token);

    // Wait for leadership before inserting (otherwise the row might land between iterations).
    await leaderTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await _insertEventStoreRowAsync(streamId, eventId, version: 1);

    await stampTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var seq = await _readCommitSequenceAsync(eventId);
    await Assert.That(seq).IsNotNull()
      .Because("polling tick must drive stamp_pending_commit_sequences; eventually the row gets a commit_sequence");

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// With nothing unstamped, the polling tick does not run the stamp.
  /// </summary>
  /// <remarks>
  /// The stamp's eligibility query sorts every unstamped row by transaction id before taking a
  /// batch, and it ran on every tick whether or not anything was unstamped: measured at about half
  /// a core per busy database under a bulk load. The partial-index existence probe costs nothing,
  /// so it decides whether the stamp runs at all.
  /// </remarks>
  [Test]
  public async Task Worker_NothingUnstamped_SkipsTheStampOnPollingTicksAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var worker = _newWorker(ConnectionString, pollingInterval: TimeSpan.FromMilliseconds(50));
    var skips = 0;
    var stamps = 0;
    var skippedThrice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStampSkipped += () => {
      if (Interlocked.Increment(ref skips) >= 3) {
        skippedThrice.TrySetResult();
      }
    };
    worker.OnStampCompleted += _ => Interlocked.Increment(ref stamps);
    await worker.StartAsync(cts.Token);

    await skippedThrice.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(stamps).IsEqualTo(0)
      .Because("the eligibility query sorts every unstamped row; with none it has nothing to do and must not run per tick");
  }

  [Test]
  public async Task Worker_DisableStamper_ExitsImmediatelyAndDoesNotAcquireLockAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var worker = _newWorker(ConnectionString, disable: true);

    await worker.StartAsync(cts.Token);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.IsLeader).IsFalse()
      .Because("DisableStamper=true must short-circuit before lock acquisition");
    await Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("worker exits cleanly when disabled");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Worker_TwoInstances_OnlyOneBecomesLeaderAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var workerA = _newWorker(ConnectionString);
    var workerB = _newWorker(ConnectionString);
    var leaderA = await _whenBecomesLeaderAsync(workerA);
    var leaderB = await _whenBecomesLeaderAsync(workerB);

    await workerA.StartAsync(cts.Token);
    await workerB.StartAsync(cts.Token);

    // Wait until at least one has fired OnBecameLeader.
    _ = await Task.WhenAny(leaderA.Task, leaderB.Task).WaitAsync(TimeSpan.FromSeconds(10));

    // Give the other a window to attempt lock acquisition and fail. With retry interval
    // 100ms, 500ms is well more than one attempt.
    await Task.Delay(500);

    var leaderCount = (workerA.IsLeader ? 1 : 0) + (workerB.IsLeader ? 1 : 0);
    await Assert.That(leaderCount).IsEqualTo(1)
      .Because("pg_try_advisory_lock is mutually exclusive — exactly one worker holds the lock");

    await workerA.StopAsync(CancellationToken.None);
    await workerB.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Two services sharing one database, each in its own schema, each get a stamper. Stamping is
  /// per schema (each service's <c>wh_event_store</c> is stamped by that service's leader), but
  /// a Postgres advisory lock is per database. With one key for every schema, the first service
  /// to elect a stamper excluded every other service's, whose events then stayed unstamped and
  /// reached their perspectives only through the unstamped-row grace window and the next
  /// backstop claim: seconds of added latency on every event, for as long as the processes ran.
  /// </summary>
  [Test]
  public async Task Worker_TwoServiceSchemasInOneDatabase_EachElectsItsOwnStamperAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    const string otherSchema = "stamper_other_service";
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync();
      await using var cmd = setup.CreateCommand();
      // Just enough of a second service's schema for its stamper's pending-work probe to run.
      cmd.CommandText =
        $"CREATE SCHEMA {otherSchema}; CREATE TABLE {otherSchema}.wh_event_store (commit_sequence BIGINT)";
      _ = await cmd.ExecuteNonQueryAsync();
    }

    var serviceA = _newWorker(ConnectionString, searchPath: "public");
    var serviceB = _newWorker(ConnectionString, searchPath: otherSchema);
    var leaderA = await _whenBecomesLeaderAsync(serviceA);
    var leaderB = await _whenBecomesLeaderAsync(serviceB);

    await serviceA.StartAsync(cts.Token);
    await leaderA.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await serviceB.StartAsync(cts.Token);

    try {
      await leaderB.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.That(serviceA.IsLeader && serviceB.IsLeader).IsTrue()
        .Because("each service's stamper leads for its own schema; neither may exclude the other");
    } finally {
      await serviceA.StopAsync(CancellationToken.None);
      await serviceB.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_LeaderStops_OtherTakesOverAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var workerA = _newWorker(ConnectionString);
    var workerB = _newWorker(ConnectionString);
    var leaderA = await _whenBecomesLeaderAsync(workerA);
    var leaderB = await _whenBecomesLeaderAsync(workerB);

    await workerA.StartAsync(cts.Token);
    await workerB.StartAsync(cts.Token);

    // Wait until SOMEONE is leader. Then stop that one.
    var firstAcquired = await Task.WhenAny(leaderA.Task, leaderB.Task).WaitAsync(TimeSpan.FromSeconds(10));
    PgCommitOrderStamperWorker leader, contender;
    Task contenderLeaderTask;
    if (firstAcquired == leaderA.Task) {
      leader = workerA;
      contender = workerB;
      contenderLeaderTask = leaderB.Task;
    } else {
      leader = workerB;
      contender = workerA;
      contenderLeaderTask = leaderA.Task;
    }

    await leader.StopAsync(CancellationToken.None);

    // The other should pick up the lock within a few retry intervals (100ms each).
    await contenderLeaderTask.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(contender.IsLeader).IsTrue()
      .Because("lock auto-releases when the holder's session closes; contender's next retry acquires");

    await contender.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Per-database fence lock. Transaction IDs are cluster-global, so a fence built on
  /// <c>pg_snapshot_xmin(pg_current_snapshot())</c> stalls behind ANY open write transaction in
  /// ANY database on the server — on a shared cluster that injects standing multi-second
  /// stamping latency from workloads that cannot possibly have written this database's
  /// <c>wh_event_store</c>. Only backends connected to THIS database can hold uncommitted rows
  /// here, so the correctness fence is the datname-scoped oldest in-flight backend xid. A write
  /// transaction held open in a different database must not delay stamping in this one.
  /// </summary>
  [Test]
  public async Task Worker_OpenTransactionInOtherDatabase_DoesNotStallStampingAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var worker = _newWorker(ConnectionString);
    var leaderTcs = await _whenBecomesLeaderAsync(worker);
    var stampPulse = new SemaphoreSlim(0);
    worker.OnStampCompleted += _ => stampPulse.Release();
    await worker.StartAsync(cts.Token);
    await leaderTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Hold a WRITE transaction open in another database on the same server. The temp-table
    // insert assigns a real xid, dragging the cluster-wide xmin horizon below every row
    // inserted after this point. Kept open only for the bounded assert window below, then
    // rolled back, so concurrently-running tests see at most a few seconds of horizon hold
    // (none at all once the fence is datname-scoped).
    var blockerCsb = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
    await using var blockerConn = new NpgsqlConnection(blockerCsb.ConnectionString);
    await blockerConn.OpenAsync();
    await using var blockerTx = await blockerConn.BeginTransactionAsync();
    await using (var blockerCmd = blockerConn.CreateCommand()) {
      blockerCmd.Transaction = blockerTx;
      blockerCmd.CommandText =
        "CREATE TEMP TABLE wh_fence_blocker(x int) ON COMMIT DROP; INSERT INTO wh_fence_blocker VALUES (1)";
      _ = await blockerCmd.ExecuteNonQueryAsync();
    }

    try {
      var streamId = (Guid)TrackedGuid.New();
      var eventId = (Guid)TrackedGuid.New();
      await _insertEventStoreRowAsync(streamId, eventId, version: 1);

      // Each iteration is gated on a stamp-completion signal (the 100 ms poll keeps them
      // coming); the row must be stamped WHILE the other-database transaction stays open.
      long? seq = null;
      var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
      while (seq is null && DateTimeOffset.UtcNow < deadline) {
        _ = await stampPulse.WaitAsync(TimeSpan.FromSeconds(5));
        seq = await _readCommitSequenceAsync(eventId);
      }
      await Assert.That(seq.HasValue).IsTrue()
        .Because("an open transaction in an unrelated database cannot have written this database's "
               + "wh_event_store — it must not fence this database's commit-sequence stamping");
    } finally {
      await blockerTx.RollbackAsync();
    }

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Stamp-until-drained lock. A wake that stamps zero rows while unstamped rows exist (the
  /// fence is held) must NOT sleep until the next external wake — in production the relaxed
  /// cadence is 30 s and the backup tick 5 s, which quantized every perspective apply to the
  /// backstop cadence whenever a commit's own <c>wh_committed</c> wake landed while any older
  /// transaction was still open. Once fenced work is observed, the worker keeps re-stamping on
  /// a tight interval until the pending set drains. "Idle" means no pending work, not no
  /// recent doorbell.
  /// </summary>
  [Test]
  public async Task Worker_FencedWake_RetriesUntilDrained_WithoutExternalWakeAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Same-database blocker opened BEFORE the row insert — a legitimate fence hold. The
    // temp-table write assigns the blocker a real xid below the row's.
    await using var blockerConn = new NpgsqlConnection(ConnectionString);
    await blockerConn.OpenAsync();
    var blockerTx = await blockerConn.BeginTransactionAsync();
    await using (var blockerCmd = blockerConn.CreateCommand()) {
      blockerCmd.Transaction = blockerTx;
      blockerCmd.CommandText =
        "CREATE TEMP TABLE wh_fence_blocker(x int) ON COMMIT DROP; INSERT INTO wh_fence_blocker VALUES (1)";
      _ = await blockerCmd.ExecuteNonQueryAsync();
    }

    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await _insertEventStoreRowAsync(streamId, eventId, version: 1);

    // 60 s floor silences the self-poll; the worker's initial wake permit produces exactly one
    // stamp attempt, which observes the fenced row and stamps zero. After the blocker rolls
    // back there is NO further external wake — only the worker's own fenced-work retry can
    // stamp the row.
    var worker = _newWorker(ConnectionString, pollingInterval: TimeSpan.FromSeconds(60));
    var leaderTcs = await _whenBecomesLeaderAsync(worker);
    var stampPulse = new SemaphoreSlim(0);
    worker.OnStampCompleted += _ => stampPulse.Release();
    await worker.StartAsync(cts.Token);
    await leaderTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Consume the initial-permit stamp attempt (fenced → 0 rows).
    _ = await stampPulse.WaitAsync(TimeSpan.FromSeconds(10));

    await blockerTx.RollbackAsync();

    long? seq = null;
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
    while (seq is null && DateTimeOffset.UtcNow < deadline) {
      _ = await stampPulse.WaitAsync(TimeSpan.FromSeconds(3));
      seq = await _readCommitSequenceAsync(eventId);
    }
    await Assert.That(seq.HasValue).IsTrue()
      .Because("a wake that leaves fenced rows behind must keep retrying until the pending set "
             + "drains — pending work must not wait for the next external tick");

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// A NOTIFY-availability flip must wake the leader at once. When the gate turns off, the relaxed
  /// cadence is no longer safe (a NOTIFY may never arrive), so the next iteration has to recompute
  /// its interval now rather than at the end of a long sleep; when it turns back on, the same wake
  /// lets the loop pick up the relaxed cadence right away. A stopped stamper must no longer listen.
  /// </summary>
  [Test]
  public async Task Worker_GateAvailabilityFlip_WakesTheLeaderWithoutWaitingForThePollAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var gate = new FakeSignalingGate();
    // A 60 s floor silences the self-poll: after the initial permit, only the gate flip can wake the loop.
    var worker = _newWorker(ConnectionString, pollingInterval: TimeSpan.FromSeconds(60), gate: gate);
    var leaderTcs = await _whenBecomesLeaderAsync(worker);
    var stampPulse = new SemaphoreSlim(0);
    // A wake with nothing unstamped is skipped rather than stamped; either is the wake this test
    // counts.
    worker.OnStampCompleted += _ => stampPulse.Release();
    worker.OnStampSkipped += () => stampPulse.Release();
    await worker.StartAsync(cts.Token);
    await leaderTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(await stampPulse.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue()
      .Because("the initial wake permit produces exactly one wake of the loop");
    await Assert.That(gate.Subscribers).IsEqualTo(1)
      .Because("the running stamper listens for availability changes");

    gate.Set(false);

    await Assert.That(await stampPulse.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue()
      .Because("the flip is the only wake source inside the 60 s floor, so a second stamp attempt proves it woke the leader");

    await worker.StopAsync(CancellationToken.None);
    await Assert.That(gate.Subscribers).IsEqualTo(0)
      .Because("a stopped stamper unsubscribes, so a live gate cannot wake a worker that no longer runs");
  }

  /// <summary>
  /// #1217: a stamp that times out halves the batch and the leader retries at once, still leading, until a call
  /// completes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A canceled call rolls back everything it did, so the same size can never succeed. The batch used to be resized
  /// only after a call that returned: a timeout left it where it was, the leader dropped its lock and came back with
  /// the same size, and on a deployed environment one stamper repeated a 30 s timeout for ten hours after an import
  /// had grown its batch, while every read model of that service stood still.
  /// </para>
  /// <para>
  /// The timeout is made certain rather than likely: a <c>SHARE</c> lock on the event store lets the stamp choose and
  /// lock its rows and stops its update, so with a one-second command timeout the call cannot finish until the lock is
  /// released. The test waits on the worker's own signals, never on time.
  /// </para>
  /// </remarks>
  [Test]
  public async Task Worker_AStampTimesOut_HalvesTheBatchAndKeepsStampingAsLeaderAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var eventId = (Guid)TrackedGuid.New();
    await _insertEventStoreRowAsync((Guid)TrackedGuid.New(), eventId, version: 1);

    await using var blocker = new NpgsqlConnection(ConnectionString);
    await blocker.OpenAsync(cts.Token);
    await using (var begin = new NpgsqlCommand("BEGIN; LOCK TABLE wh_event_store IN SHARE MODE", blocker)) {
      await begin.ExecuteNonQueryAsync(cts.Token);
    }

    var worker = _newWorker(ConnectionString + ";Command Timeout=1", batchSize: 8);
    var leaderships = 0;
    worker.OnBecameLeader += () => Interlocked.Increment(ref leaderships);
    var timedOut = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStampTimedOut += next => timedOut.TrySetResult(next);
    var stamped = _whenStampCount(worker, minStamped: 1);
    await worker.StartAsync(cts.Token);

    var nextBatch = await timedOut.Task.WaitAsync(cts.Token);
    await using (var commit = new NpgsqlCommand("COMMIT", blocker)) {
      await commit.ExecuteNonQueryAsync(cts.Token);
    }
    await stamped.Task.WaitAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(nextBatch).IsEqualTo(4)
      .Because("the call that timed out asked for 8, and a canceled call stamps nothing, so the next asks for half");
    await Assert.That(await _readCommitSequenceAsync(eventId)).IsNotNull()
      .Because("once a call can complete, the backed-off batch stamps the pending row");
    await Assert.That(leaderships).IsEqualTo(1)
      .Because("a timeout is answered by a smaller batch, not by giving up the stamper's lock and electing again");
  }

  /// <summary>
  /// The same timeout with nothing subscribed to <see cref="PgCommitOrderStamperWorker.OnStampTimedOut"/>: the backoff
  /// does not depend on an observer, and the operator is told what timed out and what the next call asks for.
  /// </summary>
  [Test]
  public async Task Worker_AStampTimesOut_WithNoObserver_LogsTheBackoffAndStillStampsAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var eventId = (Guid)TrackedGuid.New();
    await _insertEventStoreRowAsync((Guid)TrackedGuid.New(), eventId, version: 1);

    await using var blocker = new NpgsqlConnection(ConnectionString);
    await blocker.OpenAsync(cts.Token);
    await using (var begin = new NpgsqlCommand("BEGIN; LOCK TABLE wh_event_store IN SHARE MODE", blocker)) {
      await begin.ExecuteNonQueryAsync(cts.Token);
    }

    var logger = new TimeoutSignalingLogger();
    var worker = _newWorker(ConnectionString + ";Command Timeout=1", batchSize: 8, logger: logger);
    var stamped = _whenStampCount(worker, minStamped: 1);
    await worker.StartAsync(cts.Token);

    var logged = await logger.TimedOut.Task.WaitAsync(cts.Token);
    await using (var commit = new NpgsqlCommand("COMMIT", blocker)) {
      await commit.ExecuteNonQueryAsync(cts.Token);
    }
    await stamped.Task.WaitAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(logged).Contains("a stamp of 8 rows timed out").And.Contains("the next asks for 4")
      .Because("a stamper that backs off is visible as one: the warning names the size that failed and the next");
    await Assert.That(await _readCommitSequenceAsync(eventId)).IsNotNull();
  }

  /// <summary>
  /// Completes on one of the worker's warnings (the timeout by default), so a test can act on it without subscribing
  /// to the event.
  /// </summary>
  private sealed class TimeoutSignalingLogger(int eventIdToSignal = 18) : ILogger<PgCommitOrderStamperWorker> {
    public TaskCompletionSource<string> TimedOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (eventId.Id == eventIdToSignal) {
        TimedOut.TrySetResult(formatter(state, exception));
      }
    }
  }

  // ============================================================================
  // helpers
  // ============================================================================

  /// <summary>
  /// #1327: a full call that took longer than the call budget halves the next one, and the drain still finishes as
  /// leader. A clock that moves ten seconds on every reading makes every call look slow without any call being slow.
  /// </summary>
  [Test]
  public async Task Worker_AFullStampRunsPastTheCallBudget_HalvesTheBatchAndStillDrainsAsLeaderAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var eventIds = new List<Guid>();
    for (var i = 0; i < ROWS; i++) {
      var eventId = (Guid)TrackedGuid.New();
      eventIds.Add(eventId);
      await _insertEventStoreRowAsync((Guid)TrackedGuid.New(), eventId, version: 1);
    }

    var worker = _newWorker(ConnectionString, batchSize: 8, timeProvider: new TenSecondsPerReadingClock());
    var leaderships = 0;
    worker.OnBecameLeader += () => Interlocked.Increment(ref leaderships);
    var ranLong = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnStampRanLong += next => ranLong.TrySetResult(next);
    var drained = _whenStampCount(worker, minStamped: ROWS);
    await worker.StartAsync(cts.Token);

    var nextBatch = await ranLong.Task.WaitAsync(cts.Token);
    await drained.Task.WaitAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(nextBatch).IsEqualTo(4)
      .Because("the first call filled its batch of 8 and took ten seconds, past the 2.5 s budget, so the next asks for half");
    foreach (var eventId in eventIds) {
      await Assert.That(await _readCommitSequenceAsync(eventId)).IsNotNull()
        .Because("a call that ran long still committed what it stamped; the smaller calls stamp the rest");
    }
    await Assert.That(leaderships).IsEqualTo(1)
      .Because("a slow call is answered by a smaller batch, not by giving up the stamper's lock");
  }

  /// <summary>
  /// The same slow call with nothing subscribed to <see cref="PgCommitOrderStamperWorker.OnStampRanLong"/>: the batch
  /// still halves, and the operator is told what ran long, against what budget, and what the next call asks for.
  /// </summary>
  [Test]
  public async Task Worker_AFullStampRunsPastTheCallBudget_WithNoObserver_LogsTheBackoffAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    for (var i = 0; i < ROWS; i++) {
      await _insertEventStoreRowAsync((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), version: 1);
    }

    var logger = new TimeoutSignalingLogger(eventIdToSignal: STAMP_RAN_LONG);
    var worker = _newWorker(ConnectionString, batchSize: 8, logger: logger, timeProvider: new TenSecondsPerReadingClock());
    var drained = _whenStampCount(worker, minStamped: ROWS);
    await worker.StartAsync(cts.Token);

    var logged = await logger.TimedOut.Task.WaitAsync(cts.Token);
    await drained.Task.WaitAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(logged).Contains("a stamp of 8 rows took 10000 ms").And.Contains("2500 ms call budget")
      .And.Contains("the next asks for 4")
      .Because("a stamper sizing itself by time is visible as one: the warning names the call, the budget and the next size");
  }

  private const int ROWS = 20;
  private const int STAMP_RAN_LONG = 19;

  /// <summary>A clock whose every reading is ten seconds after the last, so every measured call took ten seconds.</summary>
  private sealed class TenSecondsPerReadingClock : TimeProvider {
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond * 10);
  }

  private async Task _insertEventStoreRowAsync(Guid streamId, Guid eventId, int version) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         scope, created_at)
      VALUES
        (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent',
         NULL, NOW())";
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
  }

  private async Task<long?> _readCommitSequenceAsync(Guid eventId) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null or DBNull => null,
      _ => Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture)
    };
  }
}
