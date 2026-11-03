// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The per-perspective policy for rows of archived streams (#1739) on a database the Dapper runner migrated, which has no
/// <c>fk_perspective_cursors_event</c>: the same SQL drops a <c>Drop</c> perspective's row with its parked cursor, and moves
/// an <c>Archive</c> perspective's row beside the events and back on restore, here exactly as under EF Core. The EF Core
/// twin, with every choice and edge, is <c>ArchivedStreamRowsSqlTests</c>.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
public class DapperArchivedStreamRowsTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_archived_rows";
  private const string SERVICE = "svc-dapper-archived-rows";
  private const string MODEL = "TestApp.Archive+DapperOrderModel";
  private const string PERSPECTIVE = "TestApp.Archive+DapperOrderPerspective";

  [Test]
  public async Task Drop_OnADapperMigratedDatabase_RemovesTheRowAndItsParkedCursorAsync() {
    var (connection, stream) = await _arrangeAsync(ArchivedStreamRowPolicy.Drop);
    await using var owned = connection;

    var written = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {TABLE} WHERE id = @stream", new { stream }))
      .IsEqualTo(0L);
    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(0L).Because("the dropped row's perspective loses its parked cursor, so the stream's return rebuilds the row");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(1L).Because("the latest snapshot stays as the floor of that rebuild");
  }

  [Test]
  public async Task Archive_OnADapperMigratedDatabase_MovesTheRow_AndARestoreBringsItBackAsync() {
    var (connection, stream) = await _arrangeAsync(ArchivedStreamRowPolicy.Archive);
    await using var owned = connection;
    var row = await _rowAsync(connection, stream);
    var store = _store();

    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(await _rowAsync(connection, stream)).IsNull();
    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_event_archive_rows WHERE stream_id = @stream", new { stream })).IsEqualTo(1L);

    var restored = await store.RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _rowAsync(connection, stream)).IsEqualTo(row)
      .Because("a restore brings the archived row back exactly as it was, on this engine too");
  }

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private static Task<string?> _rowAsync(NpgsqlConnection connection, Guid stream) =>
    connection.ExecuteScalarAsync<string?>($"SELECT to_jsonb(t)::text FROM {TABLE} t WHERE t.id = @stream", new { stream });

  private async Task<(NpgsqlConnection Connection, Guid Stream)> _arrangeAsync(ArchivedStreamRowPolicy policy) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync($"""
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL)
      """);
    var payload = $$"""
      [{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}","PerspectiveName":"{{PERSPECTIVE}}"}]
      """;
    await connection.ExecuteAsync("SELECT * FROM reconcile_perspective_registry(@Payload::jsonb, @Service)",
      new { Payload = payload, Service = SERVICE });
    await RetentionSettingsWriter.WriteAsync(connection, "wh_settings",
      [ArchivedStreamRowPolicies.SettingFor([new KeyValuePair<string, ArchivedStreamRowPolicy>(PERSPECTIVE, policy)])], CancellationToken.None);

    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new {
      Req = $$"""
        {
          "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
          "new_outbox_messages": [{
            "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "TestApp.Archive.OrderPlaced", "EnvelopeType": null,
            "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
            "Metadata": {}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
          }]
        }
        """
    });
    await connection.ExecuteAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@stream, '{"total": 3}'::jsonb, '{}'::jsonb, '{}'::jsonb, '2026-11-02T09:00:00Z', '2026-11-02T09:00:00Z', 1)
      """, new { stream });
    await connection.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@stream, @name, @eventId, 0, NOW())",
      new { stream, name = PERSPECTIVE, eventId });
    for (var i = 1; i <= 2; i++) {
      await connection.ExecuteAsync(
        "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
        "VALUES (@stream, @name, @id, '{}'::jsonb, @i, @i)",
        new { stream, name = PERSPECTIVE, id = i == 2 ? eventId : Guid.CreateVersion7(), i });
    }
    return (connection, stream);
  }
}
