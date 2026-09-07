using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresNotifyDebounceStatsProvider"/> against real PostgreSQL:
/// the grouped aggregate over <c>wh_notify_state</c> (migration 137) that feeds the adaptive
/// notify-debounce OTel gauges — sums for the fired/suppressed volumes, maxima for the regime.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/PostgresNotifyDebounceStatsProvider.cs</tests>
[Category("Integration")]
[Category("Shard2")]
public class PostgresNotifyDebounceStatsProviderTests : EFCoreTestBase {
  private PostgresNotifyDebounceStatsProvider _provider = null!;

  [Before(Test)]
  public async Task TestSetupAsync() {
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    _provider = new PostgresNotifyDebounceStatsProvider(dataSource);
    await Task.CompletedTask;
  }

  [Test]
  public async Task GetStatsAsync_FreshDatabase_IsEmptyAsync() {
    var stats = await _provider.GetStatsAsync();
    await Assert.That(stats.Count).IsEqualTo(0)
      .Because("no doorbells recorded yet — the debounce-state table is empty");
  }

  [Test]
  public async Task GetStatsAsync_AggregatesPerKind_SumsVolumesAndMaxesTheRegimeAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Two inbox targets: fired/suppressed SUM across them, regime = MAX (one is flooding).
    await _insertRowAsync(conn, "inbox", firedCount: 10, suppressedCount: 1, effectiveWindowMs: 50, rapidRun: 0);
    await _insertRowAsync(conn, "inbox", firedCount: 5, suppressedCount: 40, effectiveWindowMs: 7000, rapidRun: 8);
    // One outbox target, calm.
    await _insertRowAsync(conn, "outbox", firedCount: 3, suppressedCount: 0, effectiveWindowMs: 50, rapidRun: 0);

    var stats = await _provider.GetStatsAsync();
    var byKind = stats.ToDictionary(s => s.PayloadKind);

    await Assert.That(byKind.ContainsKey("inbox")).IsTrue();
    await Assert.That(byKind["inbox"].FiredCount).IsEqualTo(15L)
      .Because("fired volume is summed across the kind's live target rows");
    await Assert.That(byKind["inbox"].SuppressedCount).IsEqualTo(41L);
    await Assert.That(byKind["inbox"].MaxEffectiveWindowMs).IsEqualTo(7000)
      .Because("the regime is the MAX across targets — one inbox target flooding at the ceiling shows through");
    await Assert.That(byKind["inbox"].MaxRapidRun).IsEqualTo(8);

    await Assert.That(byKind["outbox"].FiredCount).IsEqualTo(3L);
    await Assert.That(byKind["outbox"].MaxEffectiveWindowMs).IsEqualTo(50)
      .Because("outbox is calm — the floor window, real-time delivery");
  }

  private static async Task _insertRowAsync(NpgsqlConnection conn, string kind,
      long firedCount, long suppressedCount, int effectiveWindowMs, int rapidRun) {
    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_notify_state
        (instance_id, payload_kind, last_work_at, last_attempt_at, rapid_run, effective_window_ms, fired_count, suppressed_count)
      VALUES (@id, @kind, NULL, NOW(), @rr, @ew, @fc, @sc)
      """, conn);
    cmd.Parameters.AddWithValue("id", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("kind", kind);
    cmd.Parameters.AddWithValue("rr", rapidRun);
    cmd.Parameters.AddWithValue("ew", effectiveWindowMs);
    cmd.Parameters.AddWithValue("fc", firedCount);
    cmd.Parameters.AddWithValue("sc", suppressedCount);
    await cmd.ExecuteNonQueryAsync();
  }
}
