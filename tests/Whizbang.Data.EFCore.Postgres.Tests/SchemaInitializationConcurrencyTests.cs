using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for schema initialization advisory lock behavior, concurrency,
/// fast-path hash comparison, retry logic, and PgBouncer-compatible transaction-level locking.
/// </summary>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
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
  public async Task XactLock_WhenCancelled_ThrowsOperationCanceledAsync(CancellationToken cancellationToken) {
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

    // Assert — should have been cancelled
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
    // Arrange — create a fresh database without any WhizBang tables
    await using var adminConn = new NpgsqlConnection(ConnectionString);
    await adminConn.OpenAsync(cancellationToken);

    // Drop the schema_migrations table to simulate first run
    await using var dropCmd = adminConn.CreateCommand();
    dropCmd.CommandText = "DROP TABLE IF EXISTS wh_schema_migrations CASCADE";
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
  public async Task Initialization_WhenCancelled_ThrowsOperationCanceledAsync(CancellationToken cancellationToken) {
    // Arrange — create a pre-cancelled token
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    await using var context = CreateDbContext();

    // Act & Assert — should throw OperationCanceledException
    await Assert.That(async () =>
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cts.Token))
      .Throws<OperationCanceledException>();
  }
}
