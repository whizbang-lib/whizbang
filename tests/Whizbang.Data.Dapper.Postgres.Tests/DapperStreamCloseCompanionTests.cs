// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1486 on a database the Dapper runner migrated. The Dapper schema has no
/// <c>fk_perspective_cursors_event</c>, so a close never failed here: it left the cursor on an event that no
/// longer exists and kept snapshots a rewind cannot replay from. The re-anchor and the snapshot cleanup live in
/// the shared SQL, so both engines reach the same outcome. The EF Core twin, which also covers the refusals, is
/// <c>StreamCloseSqlTests</c>.
/// </summary>
/// <docs>fundamentals/events/event-streams</docs>
public class DapperStreamCloseCompanionTests : PostgresTestBase {
  private const string PARKED = "Tests.DapperParkedPerspective";
  private const string CURRENT = "Tests.DapperCurrentPerspective";

  [Test]
  public async Task CloseStream_OnADapperMigratedDatabase_ReanchorsTheCursor_RemovesStrandedSnapshotsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var stream = Guid.CreateVersion7();
    var ids = new Guid[4];
    for (var i = 0; i < ids.Length; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    for (var i = 0; i < ids.Length; i++) {
      var type = i == 3 ? "Whizbang.Tests.MonthClosed" : "Whizbang.Tests.LedgerEntry";
      await conn.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new { Req = _commit(ids[i], stream, type) });
    }

    await conn.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@stream, @name, @id, 0, NOW())",
      new { stream, name = PARKED, id = ids[1] });
    foreach (var (name, id, version) in new[] { (PARKED, ids[0], 1), (PARKED, ids[1], 2), (CURRENT, ids[1], 2), (CURRENT, ids[2], 3) }) {
      await conn.ExecuteAsync(
        "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
        "VALUES (@stream, @name, @id, '{}'::jsonb, @version, @version)",
        new { stream, name, id, version });
    }

    var status = await conn.ExecuteScalarAsync<string>(
      "SELECT close_status FROM close_stream(@stream, 3, FALSE)", new { stream });

    await Assert.That(status).IsEqualTo("closed");
    await Assert.That(await conn.ExecuteScalarAsync<Guid?>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @stream AND perspective_name = @name",
        new { stream, name = PARKED }))
      .IsEqualTo(ids[3])
      .Because("with no foreign key to stop it, the close must still not leave the cursor on a removed event");
    await Assert.That(await _versionsAsync(conn, stream, PARKED)).IsEqualTo("2")
      .Because("the snapshot at the parked cursor is the perspective's current state and survives");
    await Assert.That(await _versionsAsync(conn, stream, CURRENT)).IsEqualTo("3")
      .Because("below the close point only the snapshot at the close point itself can still be replayed from");
  }

  private static Task<string?> _versionsAsync(NpgsqlConnection conn, Guid stream, string name) =>
    conn.ExecuteScalarAsync<string?>(
      "SELECT string_agg(snapshot_version::text, ',' ORDER BY snapshot_version) FROM wh_perspective_snapshots " +
      "WHERE stream_id = @stream AND perspective_name = @name",
      new { stream, name });

  private static string _commit(Guid eventId, Guid streamId, string eventType) => $$"""
    {
      "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
      }]
    }
    """;
}
