// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// <see cref="DapperWorkCoordinator.FetchCollectiveSinkQueueAsync"/> reads a sink stream's unprocessed collectives in
/// commit order (#963), the order the perspective worker applies them in.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
[Category("CollectiveEvents")]
public class DapperCollectiveSinkQueueTests : PostgresTestBase {
  [Test]
  public async Task FetchCollectiveSinkQueueAsync_ReturnsTheQueueInCommitOrderAsync() {
    var stream = Guid.CreateVersion7();
    var firstCommitted = Guid.Parse("00000000-0000-7000-8000-0000000000f2");
    var secondCommitted = Guid.Parse("00000000-0000-7000-8000-0000000000f1");
    var unstamped = Guid.Parse("00000000-0000-7000-8000-0000000000f0");
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await _sinkRowAsync(conn, stream, firstCommitted, commitSequence: 10);
      await _sinkRowAsync(conn, stream, secondCommitted, commitSequence: 11);
      await _sinkRowAsync(conn, stream, unstamped, commitSequence: null);
    }
    var coordinator = new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions(), NullLogger<DapperWorkCoordinator>.Instance);

    var queue = await coordinator.FetchCollectiveSinkQueueAsync(stream);

    await Assert.That(queue).IsNotNull();
    await Assert.That(queue!.Select(q => q.EventId)).IsEquivalentTo(
      [firstCommitted, secondCommitted, unstamped], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Commit order, an unstamped row last, whatever order the ids run in.");
    await Assert.That(queue[0].CommitSequence).IsEqualTo(10L);
    await Assert.That(queue[2].CommitSequence).IsNull();
  }

  private static async Task _sinkRowAsync(NpgsqlConnection conn, Guid stream, Guid eventId, long? commitSequence) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, scope, commit_sequence, created_at)
      VALUES (@eid, @sid, @sid, 'collective', 'Test.FlipCollectiveEvent, Test', (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @sid), '{}'::jsonb, @seq, now());
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @sid, '__collective__', @eid, 0, 1, 0, now());
      """;
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sid", stream);
    cmd.Parameters.AddWithValue("seq", (object?)commitSequence ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }
}
