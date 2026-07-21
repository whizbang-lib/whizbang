using Microsoft.Extensions.Configuration;
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
}
