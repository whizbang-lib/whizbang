using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Inbox acquisition must be index-bounded and deterministic (migration 138).
/// </summary>
/// <remarks>
/// <para>
/// The <c>pick</c> stage of <c>claim_orphaned_inbox</c> ranks every eligible pending row per stream on
/// every claim cycle. Without a covering index in window order that is a Seq Scan of the whole inbox
/// heap (~2.4 KB per row) plus a Sort - O(backlog) per poll. On an 88k-row import backlog it read
/// ~200 MB and took ~700 ms per call, pegging the database and starving the commit path behind it.
/// With <c>idx_inbox_pending_stream_order</c> the same query is an Index Only Scan already in window
/// order: no heap, no sort (15x fewer buffers, 2x faster, measured on a copy of that backlog).
/// </para>
/// <para>
/// Ties: bulk imports store many rows with identical <c>received_at</c>. Ordering only by
/// <c>received_at</c> made both the per-stream rank and the acquisition cut arbitrary, so the same
/// backlog could be claimed in a different order on every cycle. <c>(received_at, message_id)</c> is a
/// total order (UUIDv7 message ids are chronological at the source).
/// </para>
/// </remarks>
[Category("Shard4")]
public class InboxAcquisitionIndexSqlTests : EFCoreTestBase {
  private const string PICK_INDEX = "idx_inbox_pending_stream_order";

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Acq138_PickShape_IsAnIndexOnlyScanInWindowOrder_NotASeqScanAsync() {
    await using var conn = await _openAsync();
    await using (var seed = conn.CreateCommand()) {
      // Distinct streams, one pending unleased row each - the shape the claim cycle ranks.
      seed.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW() - (g || ' seconds')::INTERVAL,
               gen_random_uuid(), 0, NULL, NULL, NULL, 99
        FROM generate_series(1, 5000) AS g;
        ANALYZE wh_inbox;";
      await seed.ExecuteNonQueryAsync();
    }
    // Set the visibility map: the planner picks an Index Only Scan only when it expects no heap
    // fetches, and freshly inserted rows have none of their pages marked all-visible. VACUUM must
    // run outside a transaction, so it is its own command.
    await using (var vacuum = conn.CreateCommand()) {
      vacuum.CommandText = "VACUUM (ANALYZE) wh_inbox";
      await vacuum.ExecuteNonQueryAsync();
    }

    // On a small table the planner may prefer a Seq Scan, or a Bitmap scan on a narrower partial
    // index plus a cheap Sort, on cost alone. Disabling both turns the assertion into one about
    // capability: does a covering index in window order exist that serves this shape index-only,
    // with no Sort? Without 138 the remaining plans are plain Index Scans over non-covering
    // indexes plus a Sort - they fail the assertions below (the RED run produced exactly that).
    await using var explain = conn.CreateCommand();
    explain.CommandText = @"
      SET enable_seqscan = off;
      SET enable_bitmapscan = off;
      EXPLAIN (COSTS OFF)
      SELECT i.message_id,
             ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id) AS stream_seq
      FROM wh_inbox i
      WHERE (i.instance_id IS NULL OR i.lease_expiry < NOW())
        AND (i.scheduled_for IS NULL OR i.scheduled_for <= NOW())
        AND i.processed_at IS NULL";
    var lines = new List<string>();
    await using (var reader = await explain.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        lines.Add(reader.GetString(0));
      }
    }
    var plan = string.Join("\n", lines);

    await Assert.That(plan).Contains($"Index Only Scan using {PICK_INDEX}");
    await Assert.That(plan).DoesNotContain("Seq Scan on wh_inbox");
    await Assert.That(plan).DoesNotContain("Sort Key");
  }

  [Test]
  public async Task Acq138_TiedReceivedAt_IsClaimedInMessageIdOrderAsync() {
    await using var conn = await _openAsync();
    var instance = Guid.NewGuid();
    await _registerInstanceAsync(conn, instance);

    var stream = Guid.NewGuid();
    var id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
    await using (var seed = conn.CreateCommand()) {
      // Identical received_at for all three; inserted largest-id first so physical (scan) order
      // disagrees with message-id order. Only a total order claims 1 and 2 for a bound of 2.
      seed.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
        SELECT unnest(@ids), 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, TIMESTAMPTZ '2026-01-01 00:00:00+00',
               @stream, 0, NULL, NULL, NULL, 99";
      seed.Parameters.AddWithValue("ids", new[] { id3, id2, id1 });
      seed.Parameters.AddWithValue("stream", stream);
      await seed.ExecuteNonQueryAsync();
    }

    await using var claim = conn.CreateCommand();
    claim.CommandText = @"
      SELECT c.message_id
      FROM claim_orphaned_inbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000,
                                NOW() - INTERVAL '30 seconds', 2) AS c
      WHERE c.stream_id = @stream
      ORDER BY c.message_id";
    claim.Parameters.AddWithValue("inst", instance);
    claim.Parameters.AddWithValue("stream", stream);
    var claimed = new List<Guid>();
    await using (var reader = await claim.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        claimed.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(claimed).IsEquivalentTo(new[] { id1, id2 });
  }
}
