// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper coordinator's held-age reading (#2542): the arrival time of the oldest outbox event an instance holds for
/// its commit-order stamp, through <c>wh_oldest_sequence_held_outbox</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
/// <docs>fundamentals/messaging/sequence-integrity#watching-held-messages</docs>
public class DapperSequenceHoldTests : PostgresTestBase {
  [Test]
  public async Task GetOldestSequenceHeldOutbox_IsTheOldestHeldEventsArrival_OrNullAsync() {
    IWorkCoordinator coordinator = new DapperWorkCoordinator(
      ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<DapperWorkCoordinator>.Instance);
    var instance = (Guid)TrackedGuid.New();
    var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    await Assert.That(await coordinator.GetOldestSequenceHeldOutboxAsync(instance, 10)).IsNull();

    var id = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event, sequence_hold_until)
        VALUES (@id, 'orders', 'TestEvent', 'TestEnvelope', '{"payload":1}', '{"hop":1}', 1, 1,
                @at, @stream, 0, @inst, NOW() + INTERVAL '5 minutes', TRUE, 'infinity');
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
        VALUES (@id, @stream, @stream, 'TestAggregate', 1, 'TestEvent', NULL, NOW());
        """;
      cmd.Parameters.AddWithValue("id", id);
      cmd.Parameters.AddWithValue("stream", stream);
      cmd.Parameters.AddWithValue("inst", instance);
      cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
      await cmd.ExecuteNonQueryAsync();
    }

    await Assert.That(await coordinator.GetOldestSequenceHeldOutboxAsync(instance, 10)).IsEqualTo(at);
  }
}
