using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for <see cref="DapperPerspectiveStreamLocker"/> against real PostgreSQL.
/// Tests lock acquisition, contention, expiry, renewal, and release.
/// </summary>
[Category("Integration")]
public class DapperPerspectiveStreamLockerTests : IDisposable {
  private TestFixture _testBase = null!;
  private DapperPerspectiveStreamLocker _locker = null!;
  private PerspectiveStreamLockOptions _lockOptions = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
    _lockOptions = new PerspectiveStreamLockOptions {
      LockTimeout = TimeSpan.FromSeconds(30),
      KeepAliveInterval = TimeSpan.FromSeconds(10)
    };
    _locker = new DapperPerspectiveStreamLocker(
      _testBase.TestConnectionString,
      Options.Create(_lockOptions));
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    GC.SuppressFinalize(this);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  #region TryAcquireLockAsync Tests

  [Test]
  public async Task TryAcquireLockAsync_UnlockedCursor_AcquiresSuccessfullyAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "rewind");

    await Assert.That(acquired).IsTrue();
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceId, "rewind");
  }

  [Test]
  public async Task TryAcquireLockAsync_NoCursorRow_ReturnsFalseAsync() {
    // No cursor row exists — UPDATE affects 0 rows
    var acquired = await _locker.TryAcquireLockAsync(
      Guid.CreateVersion7(), "NonExistent", Guid.CreateVersion7(), "rewind");

    await Assert.That(acquired).IsFalse();
  }

  [Test]
  public async Task TryAcquireLockAsync_LockedByDifferentInstance_ReturnsFalseAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    // Instance A acquires
    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");

    // Instance B tries to acquire — should fail
    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceB, "bootstrap");

    await Assert.That(acquired).IsFalse();
    // Instance A should still hold the lock
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceA, "rewind");
  }

  [Test]
  public async Task TryAcquireLockAsync_LockedBySameInstance_ReacquiresAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "rewind");

    // Same instance re-acquires with different reason — should succeed (idempotent)
    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "bootstrap");

    await Assert.That(acquired).IsTrue();
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceId, "bootstrap");
  }

  [Test]
  public async Task TryAcquireLockAsync_ExpiredLock_AcquiresSuccessfullyAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    // Instance A acquires with short timeout (100ms provides enough margin for
    // PostgreSQL timestamp precision while still being fast for tests)
    var shortTimeoutOptions = new PerspectiveStreamLockOptions { LockTimeout = TimeSpan.FromMilliseconds(100) };
    var shortLocker = new DapperPerspectiveStreamLocker(
      _testBase.TestConnectionString, Options.Create(shortTimeoutOptions));

    await shortLocker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");

    // Wait well past expiry (500ms >> 100ms timeout) to ensure lock is expired
    // even under heavy CI load or clock skew
    await Task.Delay(500);

    // Instance B should be able to acquire the expired lock
    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceB, "bootstrap");

    await Assert.That(acquired).IsTrue();
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceB, "bootstrap");
  }

  [Test]
  public async Task TryAcquireLockAsync_MultipleCursors_OnlyLocksTargetAsync() {
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(stream1, perspectiveName);
    await _insertCursorRowAsync(stream2, perspectiveName);

    await _locker.TryAcquireLockAsync(stream1, perspectiveName, instanceId, "rewind");

    // Stream2 should still be unlocked
    await _assertNoLockAsync(stream2, perspectiveName);
  }

  #endregion

  #region RenewLockAsync Tests

  [Test]
  public async Task RenewLockAsync_HeldLock_ExtendsExpiryAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "rewind");
    var originalExpiry = await _getLockExpiryAsync(streamId, perspectiveName);

    // Small delay to get a different timestamp
    await Task.Delay(50);

    await _locker.RenewLockAsync(streamId, perspectiveName, instanceId);
    var newExpiry = await _getLockExpiryAsync(streamId, perspectiveName);

    await Assert.That(newExpiry!.Value).IsGreaterThan(originalExpiry!.Value);
  }

  [Test]
  public async Task RenewLockAsync_WrongInstance_DoesNotRenewAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");
    var originalExpiry = await _getLockExpiryAsync(streamId, perspectiveName);

    await Task.Delay(50);

    // Instance B tries to renew — should be no-op
    await _locker.RenewLockAsync(streamId, perspectiveName, instanceB);
    var expiry = await _getLockExpiryAsync(streamId, perspectiveName);

    // Expiry should be unchanged
    await Assert.That(expiry).IsEqualTo(originalExpiry);
  }

  #endregion

  #region ReleaseLockAsync Tests

  [Test]
  public async Task ReleaseLockAsync_HeldLock_ClearsAllLockFieldsAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceId = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceId, "rewind");
    await _locker.ReleaseLockAsync(streamId, perspectiveName, instanceId);

    await _assertNoLockAsync(streamId, perspectiveName);
  }

  [Test]
  public async Task ReleaseLockAsync_WrongInstance_DoesNotReleaseAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");

    // Instance B tries to release — should be no-op
    await _locker.ReleaseLockAsync(streamId, perspectiveName, instanceB);

    // Instance A should still hold the lock
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceA, "rewind");
  }

  [Test]
  public async Task ReleaseLockAsync_AlreadyUnlocked_DoesNotThrowAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    await _insertCursorRowAsync(streamId, perspectiveName);

    // Release a lock that was never acquired — should not throw
    await _locker.ReleaseLockAsync(streamId, perspectiveName, Guid.CreateVersion7());
  }

  [Test]
  public async Task ReleaseLockAsync_MakesLockAcquirableByOtherInstanceAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    // Instance A acquires and releases
    await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");
    await _locker.ReleaseLockAsync(streamId, perspectiveName, instanceA);

    // Instance B should now be able to acquire
    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceB, "bootstrap");
    await Assert.That(acquired).IsTrue();
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceB, "bootstrap");
  }

  #endregion

  #region Full Lifecycle Tests

  [Test]
  public async Task FullLifecycle_AcquireRenewReleaseReacquireAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "OrderPerspective";
    var instanceA = Guid.CreateVersion7();
    var instanceB = Guid.CreateVersion7();
    await _insertCursorRowAsync(streamId, perspectiveName);

    // 1. Acquire
    var acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceA, "rewind");
    await Assert.That(acquired).IsTrue();

    // 2. Renew 3 times
    for (var i = 0; i < 3; i++) {
      await Task.Delay(10);
      await _locker.RenewLockAsync(streamId, perspectiveName, instanceA);
    }
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceA, "rewind");

    // 3. Release
    await _locker.ReleaseLockAsync(streamId, perspectiveName, instanceA);
    await _assertNoLockAsync(streamId, perspectiveName);

    // 4. Different instance acquires
    acquired = await _locker.TryAcquireLockAsync(streamId, perspectiveName, instanceB, "bootstrap");
    await Assert.That(acquired).IsTrue();
    await _assertLockHeldByAsync(streamId, perspectiveName, instanceB, "bootstrap");
  }

  #endregion

  #region Helpers

  private async Task _insertCursorRowAsync(Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(_testBase.TestConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, status) VALUES (@StreamId, @PerspectiveName, 0)",
      new { StreamId = streamId, PerspectiveName = perspectiveName });
  }

  private async Task _assertLockHeldByAsync(Guid streamId, string perspectiveName, Guid expectedInstanceId, string expectedReason) {
    await using var connection = new NpgsqlConnection(_testBase.TestConnectionString);
    await connection.OpenAsync();
    var row = await connection.QuerySingleAsync<dynamic>(
      """
      SELECT stream_lock_instance_id, stream_lock_expiry, stream_lock_reason
      FROM wh_perspective_cursors
      WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName
      """,
      new { StreamId = streamId, PerspectiveName = perspectiveName });

    await Assert.That((Guid)row.stream_lock_instance_id).IsEqualTo(expectedInstanceId);
    await Assert.That((string)row.stream_lock_reason).IsEqualTo(expectedReason);
    await Assert.That((DateTimeOffset)row.stream_lock_expiry).IsGreaterThan(DateTimeOffset.UtcNow);
  }

  private async Task _assertNoLockAsync(Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(_testBase.TestConnectionString);
    await connection.OpenAsync();
    var row = await connection.QuerySingleAsync<dynamic>(
      """
      SELECT stream_lock_instance_id, stream_lock_expiry, stream_lock_reason
      FROM wh_perspective_cursors
      WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName
      """,
      new { StreamId = streamId, PerspectiveName = perspectiveName });

    await Assert.That((Guid?)row.stream_lock_instance_id).IsNull();
    await Assert.That((DateTimeOffset?)row.stream_lock_expiry).IsNull();
    await Assert.That((string?)row.stream_lock_reason).IsNull();
  }

  private async Task<DateTimeOffset?> _getLockExpiryAsync(Guid streamId, string perspectiveName) {
    await using var connection = new NpgsqlConnection(_testBase.TestConnectionString);
    await connection.OpenAsync();
    return await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(
      """
      SELECT stream_lock_expiry
      FROM wh_perspective_cursors
      WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName
      """,
      new { StreamId = streamId, PerspectiveName = perspectiveName });
  }

  #endregion

  private sealed class TestFixture : PostgresTestBase {
    public string TestConnectionString => ConnectionString;
  }
}
