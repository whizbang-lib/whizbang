using System.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Performance guard for inbox acquisition (migration 138). Not a PR-slice test: the Benchmark category
/// is excluded from the shard slices by construction and runs on a scheduled or manual job.
/// </summary>
/// <remarks>
/// Seeds ~100k pending rows shaped like a bulk import: a few fat streams carrying thousands of rows each,
/// tens of thousands of singleton streams, and many identical <c>received_at</c> values. Acquisition must
/// stay under budget at that size. Without 138 the pick was a Seq Scan plus Sort of every pending row and
/// took ~700 ms per call on a backlog of this size; with the covering index in window order it is ~300 ms
/// (measured on a copy of a real backlog). The budget leaves headroom for slower CI hardware while still
/// catching a return to O(backlog).
/// </remarks>
[Category("Benchmark")]
public class InboxAcquisitionBenchmarkTests : EFCoreTestBase {
  private const int FAT_STREAMS = 20;
  private const int ROWS_PER_FAT_STREAM = 3000;
  private const int SINGLETON_STREAMS = 40_000;
  private const int BOUND = 1000;
  private const int RUNS = 5;
  private const double BUDGET_MS = 500;

  [Test]
  public async Task Bench138_ClaimOrphanedInbox_At100kPending_StaysUnderBudgetAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    await using (var reg = conn.CreateCommand()) {
      reg.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
        VALUES (@inst, 'bench', 'bench-host', 1, NOW(), NOW())
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
      reg.Parameters.AddWithValue("inst", instance);
      await reg.ExecuteNonQueryAsync();
    }
    await using (var seed = conn.CreateCommand()) {
      // Fat streams: deterministic ids, received_at bucketed to 500 distinct values so ties are common.
      seed.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
        SELECT gen_random_uuid(), 'BenchHandler', 'BenchEvent', '{}', '{}', 1, 0,
               NOW() - ((g % 500) || ' seconds')::INTERVAL,
               ('00000000-0000-0000-0000-' || lpad(s::text, 12, '0'))::uuid, 0, NULL, NULL, NULL, 99
        FROM generate_series(1, @fat) AS s CROSS JOIN generate_series(1, @perFat) AS g;
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
        SELECT gen_random_uuid(), 'BenchHandler', 'BenchEvent', '{}', '{}', 1, 0,
               NOW() - ((g % 500) || ' seconds')::INTERVAL,
               gen_random_uuid(), 0, NULL, NULL, NULL, 99
        FROM generate_series(1, @singles) AS g;";
      seed.Parameters.AddWithValue("fat", FAT_STREAMS);
      seed.Parameters.AddWithValue("perFat", ROWS_PER_FAT_STREAM);
      seed.Parameters.AddWithValue("singles", SINGLETON_STREAMS);
      seed.CommandTimeout = 300;
      await seed.ExecuteNonQueryAsync();
    }
    await using (var vacuum = conn.CreateCommand()) {
      vacuum.CommandText = "VACUUM (ANALYZE) wh_inbox";
      vacuum.CommandTimeout = 300;
      await vacuum.ExecuteNonQueryAsync();
    }

    var samples = new List<double>(RUNS);
    for (var i = 0; i < RUNS; i++) {
      await using var claim = conn.CreateCommand();
      claim.CommandText = @"
        SELECT count(*) FROM claim_orphaned_inbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000,
                                                 NOW() - INTERVAL '30 seconds', @bound)";
      claim.Parameters.AddWithValue("inst", instance);
      claim.Parameters.AddWithValue("bound", BOUND);
      claim.CommandTimeout = 120;
      var sw = Stopwatch.StartNew();
      var claimed = Convert.ToInt64(await claim.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
      sw.Stop();
      samples.Add(sw.Elapsed.TotalMilliseconds);
      await Assert.That(claimed).IsEqualTo(BOUND);
    }
    samples.Sort();
    var median = samples[samples.Count / 2];

    await Assert.That(median).IsLessThan(BUDGET_MS)
      .Because($"acquisition at ~100k pending rows must stay index-bound; samples (ms): {string.Join(", ", samples.Select(s => s.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}");
  }
}
