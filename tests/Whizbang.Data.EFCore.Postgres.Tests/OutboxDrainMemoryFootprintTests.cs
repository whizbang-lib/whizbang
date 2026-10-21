// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
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
/// <para>Two measures, each used only where it cannot flake. The bytes the fetch reads from the server are
/// counted by Npgsql's own meter (<c>db.client.operation.npgsql.bytes_read</c>), filtered to this test's
/// connection pool, which no other test shares: that is what the budget bounds, and every row read is a
/// row materialized. Managed allocation (<see cref="GC.GetTotalAllocatedBytes(bool)"/>) is process-wide,
/// so concurrent tests can only add to it; it is used only for the lower bound on the unbudgeted fetch,
/// which noise can never break.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Integration")]
[NotInParallel("OutboxDrainMemoryFootprint")]   // serializes siblings; cross-test noise is handled by min-of-N sampling
[Category("Shard4")]
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

    // GetTotalAllocatedBytes is PROCESS-wide, and the rest of the assembly's tests run concurrently in this
    // process: their allocations land inside a sampled delta as additive noise, enough to push a budgeted
    // fetch past any fixed ceiling. So allocation is asserted only from below. What the budget bounds, the
    // bytes read from the server, is counted per connection pool, and this test's pool is its own.
    var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
    using var bytesRead = new PoolBytesRead(database);

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var readBefore = bytesRead.Total;
    var unbudgeted = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100);
    var unbudgetedAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    var unbudgetedRead = bytesRead.Total - readBefore;

    readBefore = bytesRead.Total;
    var budgeted = await coordinator.FetchOutboxBatchAsync([stream], instance, maxPerStream: 100, maxBytes: BYTE_BUDGET);
    var budgetedRead = bytesRead.Total - readBefore;

    await Assert.That(unbudgeted.Count).IsEqualTo(ROW_COUNT)
      .Because("precondition: the count bound alone happily hauls the whole backlog into memory");
    await Assert.That(unbudgetedAllocated).IsGreaterThan(40_000_000L)
      .Because("~30 MB of payload materializes as UTF-16 strings (~2x) plus reader buffers: this IS "
               + "the allocation spike that OOMed the live service, reproduced and measured");
    await Assert.That(unbudgetedRead).IsGreaterThan((long)ROW_COUNT * PAYLOAD_BYTES)
      .Because("the unbudgeted fetch reads every row's payload off the wire, so the meter must see all of it");

    await Assert.That(budgeted.Count).IsLessThan(ROW_COUNT)
      .Because("the budget must have actually cut the slice for the comparison to mean anything");
    await Assert.That(budgetedRead).IsLessThan(BYTE_BUDGET + (2L * PAYLOAD_BYTES))
      .Because("the cut is made in the database, so the fetch reads the budget plus at most the row that "
               + "crosses it; a client-side trim would read the whole backlog and allocate it all");
    await Assert.That(budgetedRead * 3).IsLessThan(unbudgetedRead)
      .Because("the point is the RATIO: the budget changes the fetch's memory class, not its constant");
  }

  /// <summary>Totals the bytes Npgsql reads from the server on the connection pool for one database.</summary>
  private sealed class PoolBytesRead : IDisposable {
    private readonly MeterListener _listener = new();
    private long _total;

    public PoolBytesRead(string database) {
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.operation.npgsql.bytes_read") {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        foreach (var tag in tags) {
          if (tag.Key == "db.client.connection.pool.name" && tag.Value is string pool
              && pool.Contains(database, StringComparison.Ordinal)) {
            Interlocked.Add(ref _total, value);
          }
        }
      });
      _listener.Start();
    }

    public long Total => Interlocked.Read(ref _total);

    public void Dispose() => _listener.Dispose();
  }
}
