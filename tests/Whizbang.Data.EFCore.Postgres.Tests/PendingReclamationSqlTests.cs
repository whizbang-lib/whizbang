// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How much storage cleanup has left to do (#2289): per index-served maintenance task, the rows it would remove now,
/// counted up to a cap; and per incomplete ephemeral history walk, the events it has yet to visit.
/// </summary>
/// <remarks>
/// Each task's count copies the task's own predicate and setting, so it is exactly what the next maintenance cycle would
/// remove; rows the task would keep are not counted. A count that reaches the cap stops there and says so, which is
/// what keeps one reading's cost independent of the backlog it reports.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/401_CleanupReportsWhatItHasLeft.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs</code-under-test>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
[Category("Shard1")]
public class PendingReclamationSqlTests : EFCoreTestBase {
  private NpgsqlDataSource _dataSource = null!;
  // Through the interface, as the sampler reads it.
  private ITableStatisticsProvider _provider = null!;
  private int _window;

  [Before(Test)]
  public async Task TestSetupAsync() {
    _dataSource = NpgsqlDataSource.Create(ConnectionString);
    _provider = new PostgresTableStatisticsProvider(_dataSource);
    await Task.CompletedTask;
  }

  [After(Test)]
  public async Task TestTeardownAsync() => await _dataSource.DisposeAsync();

  private Task<PendingReclamation?> _readAsync(int cap = 1_000) =>
    _provider.SamplePendingReclamationAsync(new PendingReclamationRequest($"test:pending:{++_window}", cap));

  private async Task _executeAsync(string sql) {
    await using var cmd = _dataSource.CreateCommand(sql);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task EachTask_CountsWhatItWouldRemove_AndNothingElseAsync() {
    // For each task, two rows it would remove and one it would keep.
    await _executeAsync("""
      INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES
        (gen_random_uuid(), NOW() - INTERVAL '31 days'), (gen_random_uuid(), NOW() - INTERVAL '40 days'),
        (gen_random_uuid(), NOW() - INTERVAL '1 day');
      INSERT INTO wh_instance_evictions (instance_id, evicted_at) VALUES
        (gen_random_uuid(), NOW() - INTERVAL '25 hours'), (gen_random_uuid(), NOW() - INTERVAL '3 days'),
        (gen_random_uuid(), NOW());
      INSERT INTO wh_collective_ordering_heads (stream_id, updated_at) VALUES
        (gen_random_uuid(), NOW() - INTERVAL '8 days'), (gen_random_uuid(), NOW() - INTERVAL '30 days'),
        (gen_random_uuid(), NOW());
      INSERT INTO wh_routes (route_id, event_type, hops, first_seen, last_seen) VALUES
        (gen_random_uuid(), 'T', '[]', NOW() - INTERVAL '400 days', NOW() - INTERVAL '366 days'),
        (gen_random_uuid(), 'T', '[]', NOW() - INTERVAL '900 days', NOW() - INTERVAL '800 days'),
        (gen_random_uuid(), 'T', '[]', NOW() - INTERVAL '900 days', NOW());
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, processed_at)
      VALUES
        (gen_random_uuid(), gen_random_uuid(), 'P', gen_random_uuid(), 0, 0, 1, NOW(), NOW()),
        (gen_random_uuid(), gen_random_uuid(), 'P', gen_random_uuid(), 0, 0, 1, NOW(), NOW()),
        (gen_random_uuid(), gen_random_uuid(), 'P', gen_random_uuid(), 0, 0, 1, NOW(), NULL);
      """);

    var reading = await _readAsync();

    await Assert.That(reading).IsNotNull();
    foreach (var task in new[] {
        "purge_old_deduplication", "purge_instance_evictions", "prune_collective_ordering_heads", "purge_idle_routes",
        "purge_completed_perspective_events" }) {
      await Assert.That(reading!.Tasks[task]).IsEqualTo(new PendingRows(2, Capped: false))
        .Because($"{task} counts the two rows its predicate would remove, and not the one it would keep");
    }
    await Assert.That(reading!.Tasks["convert_metadata_history"]).IsEqualTo(new PendingRows(0, Capped: false))
      .Because("#2322: the historical metadata conversion reports what it has left to read; this database holds no event");
    await Assert.That(reading!.Tasks.Count).IsEqualTo(6)
      .Because("a task whose predicate no index serves reports nothing rather than a guess");
  }

  [Test]
  public async Task ACountPastTheCap_StopsAtTheCap_AndSaysSoAsync() {
    await _executeAsync("""
      INSERT INTO wh_message_deduplication (message_id, first_seen_at)
      SELECT gen_random_uuid(), NOW() - INTERVAL '40 days' FROM generate_series(1, 5);
      INSERT INTO wh_instance_evictions (instance_id, evicted_at)
      SELECT gen_random_uuid(), NOW() - INTERVAL '3 days' FROM generate_series(1, 3);
      """);

    var reading = await _readAsync(cap: 3);

    await Assert.That(reading!.Tasks["purge_old_deduplication"]).IsEqualTo(new PendingRows(3, Capped: true))
      .Because("five rows are eligible, the count reads at most the cap, and reports at least that many");
    await Assert.That(reading.Tasks["purge_instance_evictions"]).IsEqualTo(new PendingRows(3, Capped: false))
      .Because("exactly the cap is an exact count: the count reads one row past the cap to tell the two apart");
  }

  [Test]
  public async Task UnderDebugMode_TheTasksThatSkip_ReportNothingAsync() {
    await _executeAsync("""
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('debug_mode', 'true', 'boolean')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = 'true';
      """);

    var reading = await _readAsync();

    await Assert.That(reading!.Tasks.ContainsKey("purge_completed_perspective_events")).IsFalse()
      .Because("under debug_mode the cycle keeps completed rows for inspection, so nothing is left for it to remove");
    await Assert.That(reading.Tasks.ContainsKey("purge_idle_routes")).IsFalse();
    await Assert.That(reading.Tasks.ContainsKey("convert_metadata_history")).IsFalse()
      .Because("#2322: the conversion is skipped under debug_mode");
    await Assert.That(reading.Tasks.ContainsKey("purge_old_deduplication")).IsTrue()
      .Because("the tasks debug_mode does not skip still report");
  }

  [Test]
  public async Task AnIncompleteEphemeralWalk_ReportsWhatItHasLeft_AndACompleteOneNothingAsync() {
    await _executeAsync("""
      INSERT INTO wh_type_history_walks (operation, event_type, events_total, events_matched, completed_at) VALUES
        ('ephemeral', 'Shop.CartViewed, Shop', 100, 30, NULL),
        ('ephemeral', 'Shop.PageViewed, Shop', 50, 50, NOW()),
        ('superseding', 'Shop.PriceChanged, Shop', 80, 10, NULL);
      """);

    var reading = await _readAsync();

    await Assert.That(reading!.HistoryWalkRemaining.Count).IsEqualTo(1)
      .Because("a completed walk has nothing left, and another operation's walk is not the ephemeral reclassify");
    await Assert.That(reading.HistoryWalkRemaining["Shop.CartViewed, Shop"]).IsEqualTo(70L);
  }

  [Test]
  public async Task AWindowAnotherInstanceTook_ReadsNothingAsync() {
    var request = new PendingReclamationRequest($"test:pending:taken:{Guid.NewGuid()}", 10);
    var first = await _provider.SamplePendingReclamationAsync(request);

    var second = await _provider.SamplePendingReclamationAsync(request);

    await Assert.That(first).IsNotNull();
    await Assert.That(second).IsNull()
      .Because("one instance of the fleet reads each window, so a dashboard summing instances counts the store once");
  }
}
