using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for schema initialization advisory lock behavior and concurrency.
/// Validates the pg_try_advisory_lock + exponential backoff approach for multi-pod deployments.
/// </summary>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class SchemaInitializationConcurrencyTests : EFCoreTestBase {

  [Test]
  [Timeout(30000)]
  public async Task TryAdvisoryLock_WhenLockFree_AcquiresImmediatelyAsync(CancellationToken cancellationToken) {
    // Arrange — use a unique lock ID that won't conflict
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // Act — try to acquire a free lock
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
    var result = await cmd.ExecuteScalarAsync(cancellationToken);

    // Assert — should acquire immediately
    await Assert.That(result is true).IsTrue();

    // Cleanup — release the lock
    await using var unlockCmd = conn.CreateCommand();
    unlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    await unlockCmd.ExecuteScalarAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task TryAdvisoryLock_WhenLockHeld_RetriesWithBackoffAsync(CancellationToken cancellationToken) {
    // Arrange — acquire lock from a separate connection to simulate another pod
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;

    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using var holdCmd = holdingConn.CreateCommand();
    holdCmd.CommandText = $"SELECT pg_advisory_lock({lockId})";
    await holdCmd.ExecuteNonQueryAsync(cancellationToken);

    // Act — try to acquire the same lock from another connection (should fail)
    await using var tryConn = new NpgsqlConnection(ConnectionString);
    await tryConn.OpenAsync(cancellationToken);
    await using var tryCmd = tryConn.CreateCommand();
    tryCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
    var firstAttempt = await tryCmd.ExecuteScalarAsync(cancellationToken);

    // Assert — first attempt should fail (lock is held)
    await Assert.That(firstAttempt is true).IsFalse();

    // Release the lock from the holding connection
    await using var releaseCmd = holdingConn.CreateCommand();
    releaseCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    await releaseCmd.ExecuteScalarAsync(cancellationToken);

    // Now try again — should succeed
    await using var retryCmd = tryConn.CreateCommand();
    retryCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
    var secondAttempt = await retryCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(secondAttempt is true).IsTrue();

    // Cleanup
    await using var cleanupCmd = tryConn.CreateCommand();
    cleanupCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    await cleanupCmd.ExecuteScalarAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task TryAdvisoryLock_WhenCancelled_ThrowsOperationCanceledAsync(CancellationToken cancellationToken) {
    // Arrange — acquire lock from a separate connection so try-lock will need to retry
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;

    await using var holdingConn = new NpgsqlConnection(ConnectionString);
    await holdingConn.OpenAsync(cancellationToken);
    await using var holdCmd = holdingConn.CreateCommand();
    holdCmd.CommandText = $"SELECT pg_advisory_lock({lockId})";
    await holdCmd.ExecuteNonQueryAsync(cancellationToken);

    // Act — try to acquire from another connection with a token that cancels quickly
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    await using var tryConn = new NpgsqlConnection(ConnectionString);
    await tryConn.OpenAsync(cancellationToken);

    // Simulate the retry loop behavior from the template
    var threw = false;
    try {
      var attempt = 0;
      while (true) {
        cts.Token.ThrowIfCancellationRequested();
        await using var tryCmd = tryConn.CreateCommand();
        tryCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
        var result = await tryCmd.ExecuteScalarAsync(cts.Token);
        if (result is true) {
          break;
        }

        attempt++;
        await Task.Delay(100, cts.Token);
      }
    } catch (OperationCanceledException) {
      threw = true;
    }

    // Assert — should have been cancelled
    await Assert.That(threw).IsTrue();

    // Cleanup — release holding lock
    await using var releaseCmd = holdingConn.CreateCommand();
    releaseCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    await releaseCmd.ExecuteScalarAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AdvisoryUnlock_WhenCancelled_StillReleasesLockAsync(CancellationToken cancellationToken) {
    // Arrange — acquire a lock
    var lockId = Math.Abs(Guid.NewGuid().GetHashCode()) % int.MaxValue;
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    await using var lockCmd = conn.CreateCommand();
    lockCmd.CommandText = $"SELECT pg_advisory_lock({lockId})";
    await lockCmd.ExecuteNonQueryAsync(cancellationToken);

    // Act — release the lock using CancellationToken.None (simulating the fix)
    // even though we have a "cancelled" token available
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // This should NOT throw even though cts.Token is cancelled
    await using var unlockCmd = conn.CreateCommand();
    unlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    var unlockResult = await unlockCmd.ExecuteScalarAsync(CancellationToken.None);
    await Assert.That(unlockResult is true).IsTrue();

    // Verify — lock is actually released by trying to acquire from another connection
    await using var verifyConn = new NpgsqlConnection(ConnectionString);
    await verifyConn.OpenAsync(cancellationToken);
    await using var verifyCmd = verifyConn.CreateCommand();
    verifyCmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
    var verifyResult = await verifyCmd.ExecuteScalarAsync(cancellationToken);
    await Assert.That(verifyResult is true).IsTrue();

    // Cleanup
    await using var cleanupCmd = verifyConn.CreateCommand();
    cleanupCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
    await cleanupCmd.ExecuteScalarAsync(cancellationToken);
  }

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
}
