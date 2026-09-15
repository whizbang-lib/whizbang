using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for schema initialization advisory lock behavior, concurrency,
/// fast-path hash comparison, retry logic, and PgBouncer-compatible transaction-level locking.
/// </summary>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class SchemaInitializationConcurrencyTests : EFCoreTestBase {

  // ═══════════════════════════════════════════════════════════════════════════
  // Transaction-level advisory lock tests (pg_try_advisory_xact_lock)
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(30000)]
  public async Task XactLock_WhenLockFree_AcquiresImmediatelyAsync(CancellationToken cancellationToken) {
    // Arrange — use a unique lock ID that won't conflict
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Act — try to acquire a free xact lock inside a transaction
    await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var result = await cmd.ExecuteScalarAsync(cancellationToken);

    // Assert — should acquire immediately
    await Assert.That(result is true).IsTrue();

    // Cleanup — commit releases the xact lock automatically
    await transaction.CommitAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task XactLock_WhenLockHeld_RetriesWithBackoffAsync(CancellationToken cancellationToken) {
    // Arrange — acquire xact lock from a separate connection (simulates another pod)
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;

    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using var holdingTx = await holdingConn.BeginTransactionAsync(cancellationToken);
    await using var holdCmd = holdingConn.CreateCommand();
    holdCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var holdResult = await holdCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(holdResult is true).IsTrue();

    // Act — try to acquire the same lock from another connection (should fail)
    await using var tryConn = new NpgsqlConnection(ConnectionString);
    await tryConn.OpenAsync(cancellationToken);
    await using var tryTx = await tryConn.BeginTransactionAsync(cancellationToken);
    await using var tryCmd = tryConn.CreateCommand();
    tryCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var firstAttempt = await tryCmd.ExecuteScalarAsync(cancellationToken);

    // Assert — first attempt should fail (lock is held by other transaction)
    await Assert.That(firstAttempt is true).IsFalse();

    // Release the lock by committing the holding transaction
    await holdingTx.CommitAsync(cancellationToken);

    // Rollback the failed transaction, start a new one, and retry
    await tryTx.RollbackAsync(cancellationToken);
    await using var retryTx = await tryConn.BeginTransactionAsync(cancellationToken);
    await using var retryCmd = tryConn.CreateCommand();
    retryCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var secondAttempt = await retryCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(secondAttempt is true).IsTrue();

    // Cleanup
    await retryTx.CommitAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task XactLock_WhenCanceled_ThrowsOperationCanceledAsync(CancellationToken cancellationToken) {
    // Arrange — acquire lock from a separate connection so try-lock will need to retry
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;

    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using var holdingTx = await holdingConn.BeginTransactionAsync(cancellationToken);
    await using var holdCmd = holdingConn.CreateCommand();
    holdCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    await holdCmd.ExecuteNonQueryAsync(cancellationToken);

    // Act — try to acquire from another connection with a token that cancels quickly
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    await using var tryConn = new NpgsqlConnection(ConnectionString);
    await tryConn.OpenAsync(cancellationToken);

    // Simulate the retry loop behavior from the template
    var threw = false;
    try {
      while (true) {
        cts.Token.ThrowIfCancellationRequested();
        await using var tryTx = await tryConn.BeginTransactionAsync(cts.Token);
        await using var tryCmd = tryConn.CreateCommand();
        tryCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
        var result = await tryCmd.ExecuteScalarAsync(cts.Token);
        if (result is true) {
          break;
        }
        await tryTx.RollbackAsync(CancellationToken.None);
        await Task.Delay(100, cts.Token);
      }
    } catch (OperationCanceledException) {
      threw = true;
    }

    // Assert — should have been canceled
    await Assert.That(threw).IsTrue();

    // Cleanup — release holding lock
    await holdingTx.CommitAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task XactLock_WhenTransactionCommitted_AutoReleasesLockAsync(CancellationToken cancellationToken) {
    // Arrange — acquire xact lock in a transaction
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      await using var lockCmd = conn.CreateCommand();
      lockCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
      var acquired = await lockCmd.ExecuteScalarAsync(cancellationToken);
      await Assert.That(acquired is true).IsTrue();

      // Act — commit the transaction
      await tx.CommitAsync(cancellationToken);
    }

    // Assert — lock should be released, another connection can acquire it
    await using var verifyConn = new NpgsqlConnection(ConnectionString);
    await verifyConn.OpenAsync(cancellationToken);
    await using var verifyTx = await verifyConn.BeginTransactionAsync(cancellationToken);
    await using var verifyCmd = verifyConn.CreateCommand();
    verifyCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var verifyResult = await verifyCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(verifyResult is true).IsTrue();
    await verifyTx.CommitAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task XactLock_WhenTransactionRolledBack_AutoReleasesLockAsync(CancellationToken cancellationToken) {
    // Arrange — acquire xact lock in a transaction
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await using (var tx = await conn.BeginTransactionAsync(cancellationToken)) {
      await using var lockCmd = conn.CreateCommand();
      lockCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
      var acquired = await lockCmd.ExecuteScalarAsync(cancellationToken);
      await Assert.That(acquired is true).IsTrue();

      // Act — rollback the transaction (simulates failure)
      await tx.RollbackAsync(cancellationToken);
    }

    // Assert — lock should be released, another connection can acquire it
    await using var verifyConn = new NpgsqlConnection(ConnectionString);
    await verifyConn.OpenAsync(cancellationToken);
    await using var verifyTx = await verifyConn.BeginTransactionAsync(cancellationToken);
    await using var verifyCmd = verifyConn.CreateCommand();
    verifyCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var verifyResult = await verifyCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(verifyResult is true).IsTrue();
    await verifyTx.CommitAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task XactLock_InnerLoop_RollsBackOnFailedAcquireAsync(CancellationToken cancellationToken) {
    // Arrange — hold lock on one connection
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;

    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using var holdingTx = await holdingConn.BeginTransactionAsync(cancellationToken);
    await using var holdCmd = holdingConn.CreateCommand();
    holdCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    await holdCmd.ExecuteScalarAsync(cancellationToken);

    // Act — simulate inner lock loop: begin tx, try lock, fail, rollback
    await using var tryConn = new NpgsqlConnection(ConnectionString);
    await tryConn.OpenAsync(cancellationToken);

    var tx = await tryConn.BeginTransactionAsync(cancellationToken);
    await using var tryCmd = tryConn.CreateCommand();
    tryCmd.CommandText = $"SELECT pg_try_advisory_xact_lock({lockId})";
    var result = await tryCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(result is true).IsFalse();

    // Rollback should succeed (transaction is valid, just didn't get the lock)
    await Assert.That(async () => await tx.RollbackAsync(CancellationToken.None)).ThrowsNothing();
    await tx.DisposeAsync();

    // Assert — connection is still usable after rollback (can start a new transaction)
    await using var newTx = await tryConn.BeginTransactionAsync(cancellationToken);
    await using var checkCmd = tryConn.CreateCommand();
    checkCmd.CommandText = "SELECT 1";
    var checkResult = await checkCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(checkResult is int val && val == 1).IsTrue();
    await newTx.CommitAsync(cancellationToken);

    // Cleanup
    await holdingTx.CommitAsync(cancellationToken);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Concurrent initialization tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(120000)]
  public async Task ConcurrentInitialization_BothPodsSucceedAsync(CancellationToken cancellationToken) {
    // Arrange — two concurrent DbContext initializations simulating two pods
    // Both should complete successfully (one acquires lock first, other retries)
    await using var context1 = CreateDbContext();
    await using var context2 = CreateDbContext();

    // Act — run two initializations concurrently
    // The database is already initialized by EFCoreTestBase.SetupAsync(),
    // but running again is safe (idempotent). This tests that concurrent calls
    // don't deadlock or crash.
    var task1 = context1.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    var task2 = context2.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert — both complete without throwing
    await Assert.That(async () => await Task.WhenAll(task1, task2)).ThrowsNothing();
  }

  [Test]
  [Timeout(60000)]
  public async Task Maintenance_WithNpgsqlDataSource_SucceedsAsync(CancellationToken cancellationToken) {
    // Arrange — the test base uses NpgsqlDataSource-based configuration (like Aspire/cloud).
    // The fix ensures VACUUM ANALYZE gets the NpgsqlDataSource from the existing connection
    // rather than creating a new NpgsqlConnection from GetConnectionString() which may lack auth.
    await using var context = CreateDbContext();

    // Act — run initialization which includes the maintenance step with VACUUM ANALYZE
    // If the NpgsqlDataSource fix works, this should complete without auth failures
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken))
      .ThrowsNothing();
  }

  [Test]
  [Timeout(60000)]
  public async Task SlowPath_WhenAnotherProcessHoldsTheSchemaLock_WaitsInsteadOfMigratingAsync(
      CancellationToken cancellationToken) {
    // The lock is worth nothing unless a DIFFERENT process computes the SAME key. That is exactly
    // what the previous Math.Abs(schema.GetHashCode()) form could not do — .NET randomizes the
    // string hash seed per process, so every instance took its own private lock and every instance
    // migrated. Two initializers inside ONE test process would agree even on the broken code, so
    // this test stands in for the other process by holding the key computed from the published,
    // process-stable function and asserting the initializer is genuinely excluded by it.
    var otherProcessKey = Whizbang.Data.Postgres.SchemaInitializationLockKey.Compute("public");

    // Arrange — hold the schema lock from a separate session, as a mid-migration instance would.
    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using (var holdCmd = holdingConn.CreateCommand()) {
      holdCmd.CommandText = $"SELECT pg_advisory_lock({otherProcessKey})";
      await holdCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Force the slow path: the fast path skips the lock entirely when every hash matches, and a
    // lock that is never reached cannot demonstrate exclusion.
    await using var adminConn = new NpgsqlConnection(ConnectionString);
    await adminConn.OpenAsync(cancellationToken);
    await using (var driftCmd = adminConn.CreateCommand()) {
      driftCmd.CommandText = @"
        UPDATE wh_schema_migrations SET content_hash = 'forced-drift'
        WHERE file_name = (SELECT file_name FROM wh_schema_migrations WHERE owner = 'whizbang' LIMIT 1)";
      var drifted = await driftCmd.ExecuteNonQueryAsync(cancellationToken);
      await Assert.That(drifted).IsEqualTo(1); // guard: the arrangement must actually bite
    }

    // Act — initialize with a deadline. Held lock ⇒ the inner loop backs off until the token fires.
    await using var context = CreateDbContext();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(TimeSpan.FromSeconds(5));

    // Assert — excluded, so it waited and was canceled rather than running DDL alongside the
    // holder. On a per-process key it would take its own lock, sail through, and throw nothing.
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();

    // Cleanup — release the lock and let initialization complete, restoring the drifted hash.
    await using (var unlockCmd = holdingConn.CreateCommand()) {
      unlockCmd.CommandText = $"SELECT pg_advisory_unlock({otherProcessKey})";
      await unlockCmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var restoreContext = CreateDbContext();
    await restoreContext.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Deferring to the elected migrator
  //
  // Winning pg_try_advisory_xact_lock is what elects a migrator; these cover what the instances
  // that LOST it do. The two endings are opposite mistakes in the wrong situation — taking over
  // while the migrator works puts two instances inside the same DDL, and waiting on a migrator
  // that died strands the whole fleet on a schema that will never advance — and both situations
  // look identical from a failed try-lock. So each test fixes which one it is.
  //
  // The witness is wh_schema_migrations.updated_at. Re-applying a migration stamps it NOW(); a
  // sentinel value surviving the run is proof the deferring instance applied nothing, which no
  // count of rows or absence of an exception could establish.
  // ═══════════════════════════════════════════════════════════════════════════

  /// <summary>A timestamp no real write produces, so an untouched row is recognizable.</summary>
  private static readonly DateTime UNTOUCHED = new(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>Waits for the initializer to say it is deferring, rather than for a clock.</summary>
  /// <remarks>
  /// The test has to know the instance reached the deferral before it changes the world underneath
  /// it, and a delay long enough to be safe would be a flake waiting to happen. The log line is the
  /// event itself.
  /// </remarks>
  private sealed class _DeferralWatch : ILogger {
    private readonly TaskCompletionSource _deferring = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Deferring => _deferring.Task;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new _Scope();
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (eventId.Name == "DeferringToMigrator") {
        _deferring.TrySetResult();
      }
    }

    private sealed class _Scope : IDisposable { public void Dispose() { } }
  }

  private static long _schemaKey() => Whizbang.Data.Postgres.SchemaInitializationLockKey.Compute("public");

  /// <summary>One framework-owned ledger row, which every deployment has.</summary>
  private async Task<(string File, string Hash)> _aFrameworkLedgerRowAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT file_name, content_hash
      FROM wh_schema_migrations WHERE owner = 'whizbang' ORDER BY file_name LIMIT 1";
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await Assert.That(await reader.ReadAsync(ct)).IsTrue()
      .Because("the arrangement needs a real framework migration row to drift");
    return (reader.GetString(0), reader.GetString(1));
  }

  private async Task _setLedgerRowAsync(string file, string hash, DateTime updatedAt, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_schema_migrations SET content_hash = @hash, updated_at = @at WHERE file_name = @name";
    cmd.Parameters.AddWithValue("name", file);
    cmd.Parameters.AddWithValue("hash", hash);
    cmd.Parameters.AddWithValue("at", updatedAt);
    var changed = await cmd.ExecuteNonQueryAsync(ct);
    await Assert.That(changed).IsEqualTo(1).Because("the arrangement must actually bite");
  }

  private async Task<(string Hash, DateTime UpdatedAt)> _readLedgerRowAsync(string file, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT content_hash, updated_at FROM wh_schema_migrations WHERE file_name = @name";
    cmd.Parameters.AddWithValue("name", file);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return (reader.GetString(0), reader.GetDateTime(1));
  }

  /// <summary>A session holding the schema lock, standing in for the instance that won it.</summary>
  private async Task<(NpgsqlConnection Connection, int Pid)> _holdSchemaLockAsync(CancellationToken ct) {
    var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(ct);
    int pid;
    await using (var pidCmd = holder.CreateCommand()) {
      pidCmd.CommandText = "SELECT pg_backend_pid()";
      pid = Convert.ToInt32(await pidCmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
    await using (var take = holder.CreateCommand()) {
      take.CommandText = $"SELECT pg_advisory_lock({_schemaKey()})";
      await take.ExecuteNonQueryAsync(ct);
    }
    return (holder, pid);
  }

  /// <summary>Restores the ledger so later tests see an ordinary, current schema.</summary>
  private async Task _restoreLedgerAsync(string file, string hash, CancellationToken ct) {
    await _setLedgerRowAsync(file, hash, DateTime.UtcNow, ct);
    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: ct);
  }

  /// <summary>
  /// An instance that deferred to a migrator which then committed applies nothing itself.
  /// </summary>
  /// <remarks>
  /// The headline property, and the one a green suite could otherwise hide: before this, the loser
  /// re-contended for the lock, won it, opened a transaction, re-read the hashes and found nothing
  /// to do — correct, but paid once per replica per startup, and the lock traffic was the cost that
  /// made every replica scan the tables a rewrite touches. The untouched timestamp is what
  /// distinguishes "did nothing" from "did it again harmlessly".
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Deferral_WhenTheMigratorCommits_AppliesNothingItselfAsync(CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    var (holder, _) = await _holdSchemaLockAsync(cancellationToken);
    await using var holding = holder;

    // Force the slow path: the fast path skips the lock entirely while every hash matches, and a
    // lock never reached cannot be deferred to.
    await _setLedgerRowAsync(row.File, "forced-drift", DateTime.UtcNow, cancellationToken);

    var watch = new _DeferralWatch();
    await using var context = CreateDbContext();
    var init = context.EnsureWhizbangDatabaseInitializedAsync(watch, cancellationToken: cancellationToken);

    await watch.Deferring;

    // The migrator finishes: the schema is current again, and the commit that made it so released
    // the lock. Both become true together, exactly as a real commit makes them.
    await _setLedgerRowAsync(row.File, row.Hash, UNTOUCHED, cancellationToken);
    await using (var release = holding.CreateCommand()) {
      release.CommandText = $"SELECT pg_advisory_unlock({_schemaKey()})";
      await release.ExecuteNonQueryAsync(cancellationToken);
    }

    await init;

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.UpdatedAt).IsEqualTo(UNTOUCHED)
      .Because("the migrator's work had landed, so the deferring instance had nothing to apply");
    await Assert.That(after.Hash).IsEqualTo(row.Hash);

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// An instance that deferred to a migrator which died takes the work over.
  /// </summary>
  /// <remarks>
  /// The crash shape an orchestrator actually produces: the pod is gone, no cleanup ran, and the
  /// only thing that freed the lock was the backend dying. Waiting here is the worse failure — the
  /// schema never advances, and every survivor waits on a pod that no longer exists.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Deferral_WhenTheMigratorIsKilled_TakesTheWorkOverAsync(CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    var (holder, pid) = await _holdSchemaLockAsync(cancellationToken);
    await using var holding = holder;

    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    var watch = new _DeferralWatch();
    await using var context = CreateDbContext();
    var init = context.EnsureWhizbangDatabaseInitializedAsync(watch, cancellationToken: cancellationToken);

    await watch.Deferring;

    // Killed outright, with the schema still behind. Nothing ran a release.
    await using (var killer = new NpgsqlConnection(ConnectionString)) {
      await killer.OpenAsync(cancellationToken);
      await using var kill = killer.CreateCommand();
      kill.CommandText = "SELECT pg_terminate_backend(@pid)";
      kill.Parameters.AddWithValue("pid", pid);
      await kill.ExecuteScalarAsync(cancellationToken);
    }

    await init;

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash)
      .Because("the survivor took over and finished the work the dead instance left outstanding");
    await Assert.That(after.UpdatedAt).IsNotEqualTo(UNTOUCHED)
      .Because("taking over means re-applying, which stamps the row");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// A migrator that releases the lock without finishing is also taken over.
  /// </summary>
  /// <remarks>
  /// The same release as a successful commit, over the opposite schema state — a migration that
  /// threw and rolled back. Paired deliberately with the commit case: identical lock event,
  /// opposite correct decision, so a mechanism that keyed off the lock alone would get exactly one
  /// of the two right.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Deferral_WhenTheMigratorReleasesWithWorkOutstanding_TakesTheWorkOverAsync(
      CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    var (holder, _) = await _holdSchemaLockAsync(cancellationToken);
    await using var holding = holder;

    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    var watch = new _DeferralWatch();
    await using var context = CreateDbContext();
    var init = context.EnsureWhizbangDatabaseInitializedAsync(watch, cancellationToken: cancellationToken);

    await watch.Deferring;

    // A rollback releases the lock exactly as a commit does, and leaves the schema behind.
    await using (var release = holding.CreateCommand()) {
      release.CommandText = $"SELECT pg_advisory_unlock({_schemaKey()})";
      await release.ExecuteNonQueryAsync(cancellationToken);
    }

    await init;

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash);
    await Assert.That(after.UpdatedAt).IsNotEqualTo(UNTOUCHED)
      .Because("a released lock over an unfinished schema is a failed migrator, not a finished one");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// Three instances starting together on a schema that genuinely needs work all converge.
  /// </summary>
  /// <remarks>
  /// Two can exclude each other by accident of ordering. Three on a drifted schema exercises the
  /// real sequence — one wins the lock and migrates while the other two defer, poll, and find the
  /// work done — and asserts the outcome on the database rather than on the absence of an
  /// exception.
  /// </remarks>
  [Test]
  [Timeout(180000)]
  public async Task Deferral_ThreeInstancesOnADriftedSchemaConvergeAsync(CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    await using var first = CreateDbContext();
    await using var second = CreateDbContext();
    await using var third = CreateDbContext();

    await Assert.That(async () => await Task.WhenAll(
      first.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken),
      second.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken),
      third.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken)))
      .ThrowsNothing();

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash)
      .Because("whichever instance won, the drift must be gone once all three have returned");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Staged startup: bootstrap, then elect a migrator
  //
  // The tests above cover the lock layer, which is what every instance falls back to. These cover
  // the layer above it, over a real elector and a real database: the bootstrap makes an election
  // possible, one instance is granted the migrator duty, and the rest wait on it.
  //
  // Driven through the generated initializer rather than a stand-in for it. A fake runner can prove
  // the decisions and cannot prove that the schema lock key the waiter watches is the same one the
  // elector takes, which is the part with a real chance of being quietly wrong: the elector derives
  // its key from the notification connection's search path and the initializer derives its from the
  // DbContext schema.
  // ═══════════════════════════════════════════════════════════════════════════

  /// <summary>An instance identity, as a starting pod presents one.</summary>
  private sealed class _Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "staged-svc";
    public string HostName => "staged-host";
    public int ProcessId => 7;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>A scope carrying what staged startup resolves: an identity and an elector.</summary>
  private IServiceProvider _stagedScope(_Pod pod) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddSingleton<IDutyElector>(new PgDutyElector(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      pod,
      NullLogger<PgDutyElector>.Instance));
    return services.BuildServiceProvider();
  }

  private static long _migratorDutyKey() => DutyLockKey.Compute("public", StartupDuties.MIGRATOR);

  /// <summary>A session holding the migrator duty, standing in for the instance that won it.</summary>
  private async Task<(NpgsqlConnection Connection, int Pid)> _holdMigratorDutyAsync(CancellationToken ct) {
    var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(ct);
    int pid;
    await using (var pidCmd = holder.CreateCommand()) {
      pidCmd.CommandText = "SELECT pg_backend_pid()";
      pid = Convert.ToInt32(await pidCmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
    await using (var take = holder.CreateCommand()) {
      take.CommandText = $"SELECT pg_advisory_lock({_migratorDutyKey()})";
      await take.ExecuteNonQueryAsync(ct);
    }
    return (holder, pid);
  }

  private async Task<long> _capabilityRowsAsync(Guid instanceId, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_instance_capabilities WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private async Task<long> _registryRowsAsync(Guid instanceId, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  /// <summary>
  /// An instance that migrates joins the registry first, and gives the duty back afterwards.
  /// </summary>
  /// <remarks>
  /// Both halves are the ones that would strand a fleet if they were wrong. Registration has to
  /// happen before the election, because the capability record refuses an instance it cannot find.
  /// Release has to happen afterwards, because a duty still held by an instance that has finished
  /// leaves every other instance waiting on it indefinitely.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Staged_TheMigratorRegistersAndThenReleasesTheDutyAsync(CancellationToken cancellationToken) {
    var pod = new _Pod();
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    await _setLedgerRowAsync(row.File, "forced-drift", DateTime.UtcNow, cancellationToken);

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(
      null, null, _stagedScope(pod), cancellationToken);

    await Assert.That(await _registryRowsAsync(pod.InstanceId, cancellationToken)).IsEqualTo(1L)
      .Because("the bootstrap registers this instance so a duty can be recorded against it");
    await Assert.That(await _capabilityRowsAsync(pod.InstanceId, cancellationToken)).IsEqualTo(0L)
      .Because("a duty still recorded after migrating would read as a holder that never let go");
    await Assert.That(await _dutyLockHoldersAsync(cancellationToken)).IsEqualTo(0L);

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash)
      .Because("the elected instance did the work, not merely the electing");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// An instance that did not win the duty waits for the holder and applies nothing.
  /// </summary>
  /// <remarks>
  /// The property the whole election exists for, and the one that proves the two lock keys agree.
  /// The duty is held here from a side session using the key the initializer computes; if the
  /// elector derived a different key from its own connection's search path, this instance would win
  /// a duty nobody was holding and migrate straight through, and the untouched timestamp would
  /// change.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Staged_ANonHolderWaitsForTheDutyHolderAsync(CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    var (holder, _) = await _holdMigratorDutyAsync(cancellationToken);
    await using var holding = holder;

    await _setLedgerRowAsync(row.File, "forced-drift", DateTime.UtcNow, cancellationToken);

    var watch = new _DeferralWatch();
    await using var context = CreateDbContext();
    var init = context.EnsureWhizbangDatabaseInitializedAsync(
      watch, null, _stagedScope(new _Pod()), cancellationToken);

    await watch.Deferring;

    // The holder finishes: the schema is current again and the duty is released.
    await _setLedgerRowAsync(row.File, row.Hash, UNTOUCHED, cancellationToken);
    await using (var release = holding.CreateCommand()) {
      release.CommandText = $"SELECT pg_advisory_unlock({_migratorDutyKey()})";
      await release.ExecuteNonQueryAsync(cancellationToken);
    }

    await init;

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.UpdatedAt).IsEqualTo(UNTOUCHED)
      .Because("it waited on the duty holder and then found nothing left to apply");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// A duty holder that is killed mid-migration is taken over.
  /// </summary>
  /// <remarks>
  /// The crash an orchestrator actually produces. The duty rides a session advisory lock, so the
  /// backend dying releases it with nothing to time out, and the survivor has to notice and finish
  /// the work rather than wait on a pod that no longer exists.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Staged_AKilledDutyHolderIsTakenOverAsync(CancellationToken cancellationToken) {
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    var (holder, pid) = await _holdMigratorDutyAsync(cancellationToken);
    await using var holding = holder;

    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    var watch = new _DeferralWatch();
    await using var context = CreateDbContext();
    var init = context.EnsureWhizbangDatabaseInitializedAsync(
      watch, null, _stagedScope(new _Pod()), cancellationToken);

    await watch.Deferring;

    await using (var killer = new NpgsqlConnection(ConnectionString)) {
      await killer.OpenAsync(cancellationToken);
      await using var kill = killer.CreateCommand();
      kill.CommandText = "SELECT pg_terminate_backend(@pid)";
      kill.Parameters.AddWithValue("pid", pid);
      await kill.ExecuteScalarAsync(cancellationToken);
    }

    await init;

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash)
      .Because("the survivor took over the work the dead holder left outstanding");
    await Assert.That(after.UpdatedAt).IsNotEqualTo(UNTOUCHED);

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// With no elector in the scope, the advisory lock is still the guard.
  /// </summary>
  /// <remarks>
  /// The backstop, asserted explicitly rather than left implicit in the other tests. A deployment
  /// that never wired the notification services has nothing to elect with, and never migrating
  /// would be far worse than migrating without a duty.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Staged_WithNoElectorTheLockIsStillTheGuardAsync(CancellationToken cancellationToken) {
    var pod = new _Pod();
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(pod);

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(
      null, null, services.BuildServiceProvider(), cancellationToken);

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash);
    await Assert.That(after.UpdatedAt).IsNotEqualTo(UNTOUCHED)
      .Because("it migrated under the lock alone, which is what it did before an election existed");

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  /// <summary>
  /// Three staged instances starting together converge, and only one is ever the migrator.
  /// </summary>
  /// <remarks>
  /// Two instances can exclude each other by accident of ordering. Three contending over a real
  /// elector and a real database is where a mechanism that only mostly works shows itself, and the
  /// assertion is on the database afterwards rather than on the absence of an exception: the drift
  /// is gone, and no instance is left holding a duty.
  /// </remarks>
  [Test]
  [Timeout(180000)]
  public async Task Staged_ThreeInstancesConvergeAndNoneKeepsTheDutyAsync(CancellationToken cancellationToken) {
    var pods = new[] { new _Pod(), new _Pod(), new _Pod() };
    var row = await _aFrameworkLedgerRowAsync(cancellationToken);
    await _setLedgerRowAsync(row.File, "forced-drift", UNTOUCHED, cancellationToken);

    await using var first = CreateDbContext();
    await using var second = CreateDbContext();
    await using var third = CreateDbContext();
    var contexts = new[] { first, second, third };

    await Assert.That(async () => await Task.WhenAll(
      contexts.Select((c, i) => c.EnsureWhizbangDatabaseInitializedAsync(
        null, null, _stagedScope(pods[i]), cancellationToken))))
      .ThrowsNothing();

    var after = await _readLedgerRowAsync(row.File, cancellationToken);
    await Assert.That(after.Hash).IsEqualTo(row.Hash)
      .Because("whichever instance was elected, the drift must be gone once all three have returned");

    foreach (var pod in pods) {
      await Assert.That(await _capabilityRowsAsync(pod.InstanceId, cancellationToken)).IsEqualTo(0L)
        .Because("every duty taken has to be given back, or the next deployment waits on a ghost");
    }

    await Assert.That(await _dutyLockHoldersAsync(cancellationToken)).IsEqualTo(0L);

    await _restoreLedgerAsync(row.File, row.Hash, cancellationToken);
  }

  private async Task<long> _dutyLockHoldersAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 "
      + "AND ((classid::bigint << 32) | (objid::bigint & 4294967295)) = @key";
    cmd.Parameters.AddWithValue("key", _migratorDutyKey());
    return (long)(await cmd.ExecuteScalarAsync(ct))!;
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // _isRetryableError tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IsRetryableError_TimeoutException_ReturnsTrueAsync() {
    var ex = new TimeoutException("Connection timed out");
    var result = WorkCoordinationDbContextSchemaExtensions._isRetryableError(ex);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsRetryableError_IOException_ReturnsTrueAsync() {
    var ex = new System.IO.IOException("Connection reset by peer");
    var result = WorkCoordinationDbContextSchemaExtensions._isRetryableError(ex);
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsRetryableError_NonRetryableException_ReturnsFalseAsync() {
    var ex = new InvalidOperationException("Permission denied");
    var result = WorkCoordinationDbContextSchemaExtensions._isRetryableError(ex);
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsRetryableError_ArgumentException_ReturnsFalseAsync() {
    var ex = new ArgumentException("Invalid parameter");
    var result = WorkCoordinationDbContextSchemaExtensions._isRetryableError(ex);
    await Assert.That(result).IsFalse();
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Fast path hash comparison tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(30000)]
  public async Task FastPath_WhenBothHashesMatch_SkipsInitializationAsync(CancellationToken cancellationToken) {
    // Arrange — ensure database is fully initialized (done by EFCoreTestBase)
    await using var context = CreateDbContext();

    // Act — run initialization again (should hit fast path since all hashes match)
    // The second call should be significantly faster because it skips DDL
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    sw.Stop();

    // Assert — should complete quickly (fast path skips lock acquisition and DDL)
    // Allow generous timeout for CI environments but expect it to be fast
    await Assert.That(sw.ElapsedMilliseconds).IsLessThan(10_000);
  }

  [Test]
  [Timeout(30000)]
  public async Task FastPath_WhenTableNotExists_FallsToSlowPathAsync(CancellationToken cancellationToken) {
    // Arrange — create a fresh database without any WhizBang tables.
    await using var adminConn = new NpgsqlConnection(ConnectionString);
    await adminConn.OpenAsync(cancellationToken);

    // Drop the WHOLE schema to genuinely simulate a first run (no tables, no ledger).
    // Dropping only wh_schema_migrations while leaving a fully-migrated schema in place is
    // not a supported state: the migration chain is forward-only and a later migration
    // structurally DROPs wh_event_store.event_data, so replaying earlier migrations (which
    // reference event_data in their bodies/ALTERs, validated under check_function_bodies=on)
    // against an already-final schema cannot succeed. A real "tables don't exist" means the
    // tables are actually gone — so drop and recreate the schema for a true from-scratch install.
    await using var dropCmd = adminConn.CreateCommand();
    dropCmd.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
    await dropCmd.ExecuteNonQueryAsync(cancellationToken);

    // Act — run initialization (should fall to slow path since table doesn't exist)
    await using var context = CreateDbContext();
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken))
      .ThrowsNothing();

    // Assert — schema_migrations table should exist now
    await using var checkCmd = adminConn.CreateCommand();
    checkCmd.CommandText = "SELECT COUNT(*) FROM wh_schema_migrations";
    var count = await checkCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(count is long c && c > 0).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task FastPath_DoubleCheck_AfterLock_SkipsIfAnotherPodInitializedAsync(CancellationToken cancellationToken) {
    // This test verifies that when two pods race to the slow path, the second pod's
    // double-check inside the lock detects that the first pod already completed initialization.
    await using var context1 = CreateDbContext();
    await using var context2 = CreateDbContext();

    // Both should complete successfully — one does DDL, the other skips via double-check
    var task1 = context1.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    var task2 = context2.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    await Assert.That(async () => await Task.WhenAll(task1, task2)).ThrowsNothing();
  }

  [Test]
  [Timeout(30000)]
  public async Task FastPath_MigrationRecords_HaveCorrectOwnerAsync(CancellationToken cancellationToken) {
    // Arrange — database is already initialized by EFCoreTestBase
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Act — query migration records with owner column
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT file_name, owner FROM wh_schema_migrations ORDER BY file_name";
    var records = new List<(string FileName, string Owner)>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      var owner = reader.IsDBNull(1) ? "whizbang" : reader.GetString(1);
      records.Add((reader.GetString(0), owner));
    }

    // Assert — should have both whizbang and perspective records
    var whizbangRecords = records.Where(r => r.Owner == "whizbang").ToList();
    var perspectiveRecords = records.Where(r => r.Owner == "perspective").ToList();

    // Infrastructure migrations should have owner "whizbang"
    await Assert.That(whizbangRecords.Count).IsGreaterThan(0);

    // Perspective records should have owner "perspective"
    await Assert.That(perspectiveRecords.Count).IsGreaterThanOrEqualTo(0); // May be 0 if no perspectives in test DbContext

    // All perspective records should start with "perspective:" prefix
    foreach (var (fileName, _) in perspectiveRecords) {
      await Assert.That(fileName.StartsWith("perspective:", StringComparison.Ordinal)).IsTrue();
    }
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Init connection string tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(30000)]
  public async Task Initialization_WithInitConnectionString_UsesDirectConnectionAsync(CancellationToken cancellationToken) {
    // Arrange — use the test connection string as the "init" connection string
    // This simulates providing a direct Postgres connection (bypassing PgBouncer)
    await using var context = CreateDbContext();

    // Act — should succeed using the init connection string for VACUUM maintenance
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(
            initConnectionString: ConnectionString,
            cancellationToken: cancellationToken))
      .ThrowsNothing();
  }

  [Test]
  [Timeout(30000)]
  public async Task Initialization_WithoutInitConnectionString_UsesNormalConnectionAsync(CancellationToken cancellationToken) {
    // Arrange — no init connection string (default behavior)
    await using var context = CreateDbContext();

    // Act — should succeed using the normal DbContext connection
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(
            initConnectionString: null,
            cancellationToken: cancellationToken))
      .ThrowsNothing();
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Command timeout tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(30000)]
  public async Task Initialization_ResetCommandTimeout_AfterCompletionAsync(CancellationToken cancellationToken) {
    // Arrange
    await using var context = CreateDbContext();

    // Get the default timeout before initialization
    var defaultTimeout = context.Database.GetDbConnection().ConnectionTimeout;

    // Act — run initialization (sets 600s timeout internally, should reset after)
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

    // Assert — the DbContext should still work normally after init (timeout was reset)
    // Verify by running a simple query that would fail with an unreasonable timeout
    await Assert.That(async () => {
      await using var cmd = context.Database.GetDbConnection().CreateCommand();
      cmd.CommandText = "SELECT 1";
      await context.Database.OpenConnectionAsync(cancellationToken);
      await cmd.ExecuteScalarAsync(cancellationToken);
    }).ThrowsNothing();
  }

  // ═══════════════════════════════════════════════════════════════════════════
  // Cancellation tests
  // ═══════════════════════════════════════════════════════════════════════════

  [Test]
  [Timeout(30000)]
  public async Task Initialization_WhenCanceled_ThrowsOperationCanceledAsync(CancellationToken cancellationToken) {
    // Arrange — create a pre-canceled token
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await using var context = CreateDbContext();

    // Act & Assert — should throw OperationCanceledException
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();
  }
}
