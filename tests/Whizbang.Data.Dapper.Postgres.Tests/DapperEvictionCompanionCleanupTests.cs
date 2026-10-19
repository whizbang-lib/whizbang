// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1483 on a database the Dapper runner migrated: the eviction companion cleanup lives in the
/// shared SQL, so an eviction removes the evicted row's cursors and all but its latest snapshot here
/// exactly as it does under EF Core, and leaves another model's perspective on the same stream alone.
/// The EF Core twin, which covers every eviction path, is <c>EvictionCompanionCleanupSqlTests</c>.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention#what-an-eviction-removes</docs>
public class DapperEvictionCompanionCleanupTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_evict_companion";
  private const string SERVICE = "svc-dapper-evict";
  private const string MODEL = "TestApp.Retention+DapperEvictModel";
  private const string PERSPECTIVE = "TestApp.Retention+DapperEvictPerspective";
  private const string OTHER_PERSPECTIVE = "TestApp.Retention+DapperOtherPerspective";

  [Test]
  public async Task Cascade_OnADapperMigratedDatabase_RemovesCursor_KeepsLatestSnapshotAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync($"""
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
    await conn.ExecuteAsync("SELECT * FROM reconcile_perspective_registry(@Payload::jsonb, @Service)",
      new { Payload = payload, Service = SERVICE });

    var stream = Guid.CreateVersion7();
    await conn.ExecuteAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@stream, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, NOW(), NOW(), 1)
      """, new { stream });
    foreach (var name in new[] { PERSPECTIVE, OTHER_PERSPECTIVE }) {
      await conn.ExecuteAsync(
        "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, status, processed_at) VALUES (@stream, @name, 0, NOW())",
        new { stream, name });
      for (var i = 1; i <= 2; i++) {
        await conn.ExecuteAsync(
          "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
          "VALUES (@stream, @name, @id, '{}'::jsonb, @i, @i)",
          new { stream, name, id = Guid.CreateVersion7(), i });
      }
    }

    var deleted = await conn.ExecuteScalarAsync<int>(
      "SELECT cascade_delete_perspective_rows(@Table, @Ids)", new { Table = TABLE, Ids = new[] { stream } });

    await Assert.That(deleted).IsEqualTo(1);
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE)).IsEqualTo(0)
      .Because("the evicted row's perspective loses its cursor, so the next event rebuilds the row");
    await Assert.That(await conn.ExecuteScalarAsync<int?>(
        "SELECT snapshot_version FROM wh_perspective_snapshots WHERE stream_id = @stream AND perspective_name = @name",
        new { stream, name = PERSPECTIVE }))
      .IsEqualTo(2)
      .Because("only the latest snapshot survives, as the floor the rebuild restores from");
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, OTHER_PERSPECTIVE)).IsEqualTo(1)
      .Because("a perspective of another model on the same stream is untouched");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, OTHER_PERSPECTIVE)).IsEqualTo(2);
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, string table, Guid stream, string name) =>
    conn.ExecuteScalarAsync<long>(
      $"SELECT COUNT(*) FROM {table} WHERE stream_id = @stream AND perspective_name = @name", new { stream, name });
}
