using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
      bool disable = false) {
    var notificationOptions = new WhizbangNotificationOptions {
      DirectConnectionString = connectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(100),
      LeaderElectionRetry = leaderElectionRetry ?? TimeSpan.FromMilliseconds(100),
      BatchSize = 100,
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
      NullLogger<PgCommitOrderStamperWorker>.Instance);
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

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(streamId, eventId, version: 1);

    await stampTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

    var seq = await _readCommitSequenceAsync(eventId);
    await Assert.That(seq).IsNotNull()
      .Because("polling tick must drive stamp_pending_commit_sequences; eventually the row gets a commit_sequence");

    await worker.StopAsync(CancellationToken.None);
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
    var firstLeader = await Task.WhenAny(leaderA.Task, leaderB.Task).WaitAsync(TimeSpan.FromSeconds(10));

    // Give the other a window to attempt lock acquisition and fail. With retry interval
    // 100ms, 500ms is well more than one attempt.
    await Task.Delay(500);

    var leaderCount = (workerA.IsLeader ? 1 : 0) + (workerB.IsLeader ? 1 : 0);
    await Assert.That(leaderCount).IsEqualTo(1)
      .Because("pg_try_advisory_lock is mutually exclusive — exactly one worker holds the lock");

    await workerA.StopAsync(CancellationToken.None);
    await workerB.StopAsync(CancellationToken.None);
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
      var streamId = (Guid)TrackedGuid.NewMedo();
      var eventId = (Guid)TrackedGuid.NewMedo();
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

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
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

  // ============================================================================
  // helpers
  // ============================================================================

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
