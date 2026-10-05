// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator's continuation (#917): a stream this instance holds the lease on is moved on
/// by its next run, returning only the rows after the drain's cursor.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public class DapperOutboxStreamContinuationTests : PostgresTestBase {

  private DapperWorkCoordinator _build() => new(
    ConnectionString,
    Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
    NullLogger<DapperWorkCoordinator>.Instance);

  [Test]
  public async Task ContinueOutboxStreamsAsync_LeasesAndReturnsTheRunAfterTheCursorAsync() {
    var coordinator = _build();
    var instance = Guid.CreateVersion7();
    var stream = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).Order().ToArray();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Rows 1-2 were leased by the claim and published (completion pending); the stream's lease is this
    // instance's. Rows 3-6 are unleased.
    await conn.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW());
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@stream, 0, @inst, NOW(), NOW() + INTERVAL '5 minutes');
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number,
         instance_id, lease_expiry)
      SELECT m.id, 'test-topic', 'TestEvent', '{}', '{}', 1, CASE WHEN m.ord <= 2 THEN 1 ELSE 0 END,
             TIMESTAMPTZ '2026-01-01 00:00:00+00' + (m.ord * INTERVAL '1 millisecond'), @stream, 0,
             CASE WHEN m.ord <= 2 THEN @inst END,
             CASE WHEN m.ord <= 2 THEN NOW() + INTERVAL '5 minutes' END
      FROM unnest(@ids) WITH ORDINALITY AS m(id, ord);",
      new { inst = instance, stream, ids });

    var rows = await coordinator.ContinueOutboxStreamsAsync(
      [new OutboxStreamCursor(stream, ids[1])], instance, runLength: 3, maxBytes: null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo(ids.Skip(2).Take(3), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the next run, in order, and never a published row awaiting its completion");
    await Assert.That(rows[0].StreamId).IsEqualTo(stream);
    await Assert.That(rows[0].Destination).IsEqualTo("test-topic");
    await Assert.That(rows[0].Attempts).IsEqualTo(1);
    var leased = await conn.ExecuteScalarAsync<int>(
      "SELECT count(*) FROM wh_outbox WHERE instance_id = @inst", new { inst = instance });
    await Assert.That(leased).IsEqualTo(5);
  }

  [Test]
  public async Task ContinueOutboxStreamsAsync_NoStreams_ReturnsNothingAsync() {
    var rows = await _build().ContinueOutboxStreamsAsync([], Guid.CreateVersion7(), runLength: 3, maxBytes: null);

    await Assert.That(rows).IsEmpty();
  }
}
