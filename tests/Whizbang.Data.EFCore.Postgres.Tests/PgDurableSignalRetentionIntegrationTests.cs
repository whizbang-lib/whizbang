using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the retention sweep contract:
/// - Old rows &lt;= MIN(cursor.last_delivered_signal_id) get deleted.
/// - Old rows &gt; MIN(cursor.last_delivered_signal_id) stay, even if past the age window
///   (a slow tail must not lose its rows).
/// - Fresh rows (younger than retention) stay regardless of cursor state.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard1")]
public class PgDurableSignalRetentionIntegrationTests : EFCoreTestBase {
  private async Task<long> _insertSignalAsync(string wireName, DateTimeOffset createdAt) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_signals (wire_name, created_at) VALUES (@w, @c) RETURNING id", conn);
    cmd.Parameters.AddWithValue("w", wireName);
    cmd.Parameters.AddWithValue("c", createdAt);
    var id = await cmd.ExecuteScalarAsync();
    return Convert.ToInt64(id ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task _upsertCursorAsync(Guid instanceId, long lastId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id, updated_at)
      VALUES (@id, @last, NOW())
      ON CONFLICT (instance_id) DO UPDATE
        SET last_delivered_signal_id = EXCLUDED.last_delivered_signal_id,
            updated_at = NOW();", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("last", lastId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<bool> _signalStillExistsAsync(long id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_signals WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    return count > 0;
  }

  private PgDurableSignalRetentionWorker _createWorker() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgDurableSignalRetentionWorker(
      Options.Create(opts), cfg,
      NullLogger<PgDurableSignalRetentionWorker>.Instance);
  }

  [Test]
  public async Task Sweep_DeletesOldRowsBelowMinCursorAsync() {
    var oldId = await _insertSignalAsync("utest-retention-old-42781", DateTimeOffset.UtcNow.AddDays(-14));
    await _upsertCursorAsync(Guid.NewGuid(), oldId);   // cursor is past the old row

    var deleted = await _createWorker().SweepOnceAsync(CancellationToken.None);

    await Assert.That(deleted).IsGreaterThanOrEqualTo(1);
    await Assert.That(await _signalStillExistsAsync(oldId)).IsFalse();
  }

  [Test]
  public async Task Sweep_KeepsRowsAboveMinCursorEvenIfOldAsync() {
    // Row 1 is old AND unseen by all pods — kept because the min cursor is 0 (never advanced past it).
    var oldUnseenId = await _insertSignalAsync("utest-retention-oldunseen-11322", DateTimeOffset.UtcNow.AddDays(-14));

    // Insert a live cursor that is at 0 — has NEVER seen anything. Retention must not delete
    // this pod's data.
    await _upsertCursorAsync(Guid.NewGuid(), 0);

    var deleted = await _createWorker().SweepOnceAsync(CancellationToken.None);

    await Assert.That(deleted).IsEqualTo(0);
    await Assert.That(await _signalStillExistsAsync(oldUnseenId)).IsTrue()
      .Because("a slow tail (cursor at 0) must not lose its rows even if they are past the age window");
  }

  [Test]
  public async Task Sweep_KeepsFreshRowsRegardlessOfCursorAsync() {
    var freshId = await _insertSignalAsync("utest-retention-fresh-99311", DateTimeOffset.UtcNow.AddSeconds(-30));
    await _upsertCursorAsync(Guid.NewGuid(), freshId);

    var deleted = await _createWorker().SweepOnceAsync(CancellationToken.None);

    // The delete predicate requires created_at < NOW() - 7 days, so 30 seconds ago is safe.
    await Assert.That(await _signalStillExistsAsync(freshId)).IsTrue();
    _ = deleted;
  }

  /// <summary>
  /// Counts the worker's sweep-failure warnings (EventId 1) so a shutdown can be told apart from a
  /// sweep that genuinely failed. The two are logged by different arms of the same catch chain.
  /// </summary>
  private sealed class SweepLogRecorder : ILogger<PgDurableSignalRetentionWorker> {
    private int _sweepFailures;
    public int SweepFailures => Volatile.Read(ref _sweepFailures);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      if (eventId.Id == 1) {
        Interlocked.Increment(ref _sweepFailures);
      }
    }
  }

  /// <summary>
  /// Blocks the sweep's DELETE by holding an ACCESS EXCLUSIVE lock on <c>wh_signals</c> from a
  /// separate session, and reports (through pg_stat_activity) exactly when the worker's statement
  /// is parked on that lock. That is the "a sweep is in flight right now" signal the loop itself
  /// does not emit — without it, cancelling mid-sweep is a guess against how fast the database is.
  /// </summary>
  private async Task<bool> _waitForBlockedSweepAsync(TimeSpan timeout) {
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline) {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync();
      await using var cmd = new NpgsqlCommand(@"
        SELECT COUNT(*) FROM pg_stat_activity
        WHERE datname = current_database()
          AND pid <> pg_backend_pid()
          AND wait_event_type = 'Lock'
          AND query LIKE '%DELETE FROM wh_signals%'", conn);
      var blocked = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
      if (blocked > 0) {
        return true;
      }
      await Task.Delay(50);
    }
    return false;
  }

  /// <summary>
  /// Shutdown arriving while a sweep is mid-statement must end the worker, not be filed as a failed
  /// sweep. The distinction is not cosmetic: the sweep-failure arm logs a warning and comes back
  /// round, so every rolling deploy would emit a spurious "sweep failed" warning per pod, and the
  /// loop would keep issuing DELETEs against a database connection the host is tearing down.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_ShutdownDuringASweep_EndsTheLoopWithoutReportingASweepFailureAsync() {
    // Hold wh_signals against the sweep from an independent session so the DELETE parks
    // indefinitely rather than finishing before the cancellation can land.
    await using var blocker = new NpgsqlConnection(ConnectionString);
    await blocker.OpenAsync();
    await using var blockingTx = await blocker.BeginTransactionAsync();
    await using (var lockCmd = new NpgsqlCommand("LOCK TABLE wh_signals IN ACCESS EXCLUSIVE MODE", blocker, blockingTx)) {
      await lockCmd.ExecuteNonQueryAsync();
    }

    var logger = new SweepLogRecorder();
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var worker = new PgDurableSignalRetentionWorker(Options.Create(opts), cfg, logger) {
      SweepInterval = TimeSpan.FromMilliseconds(50),
    };

    await worker.StartAsync(CancellationToken.None);
    try {
      var reachedTheLock = await _waitForBlockedSweepAsync(TimeSpan.FromSeconds(30));
      await Assert.That(reachedTheLock).IsTrue()
        .Because("the test only means anything if the cancellation lands while the sweep's DELETE is genuinely in flight; without the blocked statement it would be cancelling an idle loop");

      // Cancels the worker's stopping token while its DELETE is parked on the lock. The bounded
      // token is only so a regression fails as an assertion instead of hanging the suite —
      // StopAsync waits on the loop task, which is exactly what is under test here.
      using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      await worker.StopAsync(stopCts.Token);

      var loop = worker.ExecuteTask;
      await Assert.That(loop is null).IsFalse();
      await loop!.WaitAsync(TimeSpan.FromSeconds(30))
        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

      await Assert.That(loop.IsCompleted).IsTrue()
        .Because("the worker must actually stop when the host stops, not sit on a cancelled connection");
      await Assert.That(loop.IsFaulted).IsFalse()
        .Because("an orderly shutdown must not surface to the host as a faulted background service");
      await Assert.That(logger.SweepFailures).IsEqualTo(0)
        .Because("a cancelled sweep is a shutdown, not a failure — routing it through the failure arm would warn on every deploy and hide real sweep breakage in the noise");
    } finally {
      await blockingTx.RollbackAsync();
    }
  }
}
