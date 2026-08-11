using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Reproduces the drain-fetch OOM mechanism as a MEASURED memory benchmark, not a container
/// experiment: the same fetch, against the same rows, allocates orders of magnitude more managed
/// heap without the byte budget than with it. This is the numeric form of the live failure — a
/// service OOM-looping through a raised memory limit because the count-bounded fetch of queued
/// control-plane messages (redelivery composites carrying whole event pages) was itself the
/// allocation that killed the process, so every restart re-fetched the same slice and died on it.
///
/// <para>Allocation is measured with <see cref="GC.GetTotalAllocatedBytes(bool)"/> deltas, which
/// counts every managed allocation on the process regardless of collection — deterministic where
/// container RSS limits are noisy. The assertions use wide margins (the unbudgeted fetch
/// materializes ~2x payload bytes as UTF-16 strings; the budgeted one is bounded by the budget),
/// so scheduler noise and unrelated test allocations cannot flip them.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Integration")]
[NotInParallel("OutboxDrainMemoryFootprint")]   // process-wide allocation counter — no concurrent pollution
public class OutboxDrainMemoryFootprintTests : EFCoreTestBase {

  private const int ROW_COUNT = 30;
  private const int PAYLOAD_BYTES = 1_000_000;   // 30 rows x ~1 MB JSON = ~30 MB of payload
  private const long BYTE_BUDGET = 4L * 1024 * 1024;   // the production MaxBytesPerStream default

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, Guid messageId,
                                       Guid instanceId, int payloadBytes) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status,
         attempts, created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
      VALUES (@id, 'topic-x', 'T, A', 'E, A',
              ('{"p":"' || repeat('x', @bytes) || '"}')::jsonb, '{}'::jsonb, '{}'::jsonb, 3,
              0, NOW(), @stream, 7, @inst, NOW() + INTERVAL '5 minutes', false)
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("bytes", payloadBytes);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task ByteBudget_BoundsTheFetchsManagedAllocation_ByOrdersOfMagnitudeAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var stream = Guid.Parse("4a4a4a4a-5555-4555-8555-555555555555");
    var instance = Guid.Parse("4b4b4b4b-5555-4555-8555-555555555555");

    for (var i = 1; i <= ROW_COUNT; i++) {
      await _seedAsync(conn, stream, Guid.Parse($"019f2222-0000-7000-8000-{i:D12}"), instance, PAYLOAD_BYTES);
    }

    // Warm both paths once so first-call machinery (query plans, reader buffers, lazy statics)
    // doesn't land in either measured delta.
    _ = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 1);
    _ = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 1, maxBytes: BYTE_BUDGET);

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var unbudgeted = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100);
    var unbudgetedAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

    before = GC.GetTotalAllocatedBytes(precise: true);
    var budgeted = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: BYTE_BUDGET);
    var budgetedAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

    await Assert.That(unbudgeted.Count).IsEqualTo(ROW_COUNT)
      .Because("precondition: the count bound alone happily hauls the whole backlog into memory");
    await Assert.That(unbudgetedAllocated).IsGreaterThan(40_000_000L)
      .Because("~30 MB of payload materializes as UTF-16 strings (~2x) plus reader buffers — this IS "
               + "the allocation spike that OOMed the live service, reproduced and measured");

    await Assert.That(budgeted.Count).IsLessThan(ROW_COUNT)
      .Because("the budget must have actually cut the slice for the comparison to mean anything");
    await Assert.That(budgetedAllocated).IsLessThan(15_000_000L)
      .Because("a 4 MB budget bounds the materialized slice (~2x budget as UTF-16 + overhead) — "
               + "the same fetch is no longer capable of the killing allocation");
    await Assert.That(budgetedAllocated * 3).IsLessThan(unbudgetedAllocated)
      .Because("the point is the RATIO: the budget changes the fetch's memory class, not its constant");
  }
}
