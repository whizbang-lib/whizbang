// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1483: a row-retention eviction reclaims storage and nothing else. The read model stays a
/// function of the events, so when a sweep or a cascade evicts row R of model M, each perspective of M
/// loses its cursor for stream R (the next event rebuilds the row instead of folding onto nothing) and
/// keeps only its latest snapshot for R (the floor that rebuild restores from). Cursors and snapshots of
/// other perspectives on the same stream are not touched, and neither are those of rows that survive.
/// </summary>
/// <remarks>
/// The registry is seeded through <c>reconcile_perspective_registry</c> with the payload the generator
/// emits, so the model-to-perspective mapping these tests depend on arrives the way it does in an
/// application. Names are nested on purpose: a top-level type hides every CLR-form bug.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention#what-an-eviction-removes</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/112_StreamGroupCascadeAndApplyPathFold.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/113_CapSweepSafetyAndFoldWatermarks.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/142_ReconcileAdoptsDottedRegistryKey.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/209_SnapshotsCarryTheirStreamVersion.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EvictionCompanionCleanup")]
[Category("Shard4")]
public class EvictionCompanionCleanupSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_evict_companion";
  private const string SERVICE = "svc-evict-companion";
  private const string MODEL = "TestApp.Retention+EvictModel";
  private const string PERSPECTIVE_A = "TestApp.Retention+EvictPerspectiveA";
  private const string PERSPECTIVE_B = "TestApp.Retention+EvictPerspectiveB";
  private const string OTHER_PERSPECTIVE = "TestApp.Retention+OtherModelPerspective";

  // ---------------------------------------------------------------------------------------------
  // Each eviction path

  [Test]
  public async Task TtlReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync() {
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: 60 * 60 * 24 * 60, cap: null);
    var (evicted, survivor) = await _seedTwoStreamsAsync(conn, evictedUpdatedDaysAgo: 90, survivorUpdatedDaysAgo: 1);

    await _executeAsync(conn, "SELECT reap_enrolled_perspective_rows()");

    await _assertCleanedAsync(conn, evicted, survivor);
  }

  [Test]
  public async Task CapReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync() {
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: null, cap: 1);
    var (evicted, survivor) = await _seedTwoStreamsAsync(conn, evictedUpdatedDaysAgo: 50, survivorUpdatedDaysAgo: 1);

    await _executeAsync(conn, "SELECT reap_perspective_row_caps()");

    await _assertCleanedAsync(conn, evicted, survivor);
  }

  [Test]
  public async Task Cascade_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync() {
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: null, cap: null);
    var (evicted, survivor) = await _seedTwoStreamsAsync(conn, evictedUpdatedDaysAgo: 1, survivorUpdatedDaysAgo: 1);

    await using var cmd = new NpgsqlCommand("SELECT cascade_delete_perspective_rows(@t, @ids)", conn);
    cmd.Parameters.AddWithValue("t", TABLE);
    cmd.Parameters.AddWithValue("ids", new[] { evicted });
    var deleted = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

    await Assert.That(deleted).IsEqualTo(1)
      .Because("the cascade still reports the rows it destroyed, not the companions it cleaned");
    await _assertCleanedAsync(conn, evicted, survivor);
  }

  [Test]
  public async Task StampedExpiryReap_RemovesTheEvictedRowsCursors_KeepsItsLatestSnapshot_LeavesTheRestAsync() {
    // perform_maintenance reaps rows whose own write path stamped expires_at (TtlRow perspectives):
    // a fourth eviction path, held to the same rule.
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: 3600, cap: null, acknowledged: false);
    var evicted = Guid.CreateVersion7();
    var survivor = Guid.CreateVersion7();
    await _seedRowAsync(conn, evicted, updatedDaysAgo: 1, expiresInHours: -1);
    await _seedRowAsync(conn, survivor, updatedDaysAgo: 1, expiresInHours: 1);
    await _seedCompanionsAsync(conn, evicted);
    await _seedCompanionsAsync(conn, survivor);

    await _executeAsync(conn, "SELECT * FROM perform_maintenance()");

    await _assertCleanedAsync(conn, evicted, survivor);
  }

  // ---------------------------------------------------------------------------------------------
  // Which snapshot survives, and when nothing is cleaned

  [Test]
  public async Task Eviction_KeepsTheSnapshotARewindCanPlace_NotANewerVersionlessOneAsync() {
    // A snapshot written before snapshots carried their stream version cannot be placed in the stream,
    // so the rewind refuses it. Keeping it as "the latest" would leave the rebuild with no usable floor.
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: null, cap: null);
    var stream = Guid.CreateVersion7();
    await _seedRowAsync(conn, stream, updatedDaysAgo: 1, expiresInHours: null);
    var older = await _seedSnapshotAsync(conn, stream, PERSPECTIVE_A, sequence: 1, version: 3);
    var placed = await _seedSnapshotAsync(conn, stream, PERSPECTIVE_A, sequence: 2, version: 7);
    var versionless = await _seedSnapshotAsync(conn, stream, PERSPECTIVE_A, sequence: 3, version: null);

    await _cascadeAsync(conn, stream);

    var remaining = await _snapshotIdsAsync(conn, stream, PERSPECTIVE_A);
    await Assert.That(remaining).Count().IsEqualTo(1);
    await Assert.That(remaining[0]).IsEqualTo(placed)
      .Because("the kept snapshot is the one the rebuild will restore from: highest stream version");
    await Assert.That(remaining.Contains(older) || remaining.Contains(versionless)).IsFalse();
  }

  [Test]
  public async Task Eviction_ModelWithNoKnownPerspectives_RemovesOnlyTheRowAsync() {
    // A registry row written before the generator carried perspective names has no mapping. The sweep
    // must not guess one: it evicts the row exactly as before and leaves every companion alone.
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: null, cap: null, withPerspectiveNames: false);
    var stream = Guid.CreateVersion7();
    await _seedRowAsync(conn, stream, updatedDaysAgo: 1, expiresInHours: null);
    await _seedCompanionsAsync(conn, stream);

    await _cascadeAsync(conn, stream);

    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse();
    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE_A)).IsEqualTo(1);
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE_A)).IsEqualTo(3);
  }

  [Test]
  public async Task Cascade_NoRowsDeleted_TouchesNoCompanionsAsync() {
    await using var conn = await _openAsync();
    await _arrangeAsync(conn, ttlSeconds: null, cap: null);
    var stream = Guid.CreateVersion7();
    await _seedCompanionsAsync(conn, stream);   // companions with no row: the cascade finds nothing to delete

    await _cascadeAsync(conn, stream);

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", stream, PERSPECTIVE_A)).IsEqualTo(1)
      .Because("only an eviction that happened cleans; a row id that matched nothing evicted nothing");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", stream, PERSPECTIVE_A)).IsEqualTo(3);
  }

  // ---------------------------------------------------------------------------------------------
  // The mapping: reconcile carries perspective names into the registry

  [Test]
  public async Task Reconcile_RecordsEveryPerspectiveOfTheModelAsync() {
    await using var conn = await _openAsync();
    await _reconcileAsync(conn, withPerspectiveNames: true);

    var names = await _perspectiveNamesAsync(conn);

    string[] expected = [PERSPECTIVE_A, PERSPECTIVE_B];
    await Assert.That(names).IsEquivalentTo(expected)
      .Because("two perspectives fold the same model; an eviction of its row must reach both");
  }

  [Test]
  public async Task Reconcile_PayloadWithoutPerspectiveNames_KeepsTheRecordedOnesAsync() {
    await using var conn = await _openAsync();
    await _reconcileAsync(conn, withPerspectiveNames: true);

    await _reconcileAsync(conn, withPerspectiveNames: false);

    string[] expected = [PERSPECTIVE_A, PERSPECTIVE_B];
    await Assert.That(await _perspectiveNamesAsync(conn)).IsEquivalentTo(expected)
      .Because("a payload that says nothing about perspectives is not evidence that there are none");
  }

  [Test]
  public async Task Reconcile_ChangedPerspectiveNames_ReplaceTheRecordedOnesAsync() {
    await using var conn = await _openAsync();
    await _reconcileAsync(conn, withPerspectiveNames: true);

    await _reconcileAsync(conn, withPerspectiveNames: true, names: [PERSPECTIVE_B]);

    string[] expected = [PERSPECTIVE_B];
    await Assert.That(await _perspectiveNamesAsync(conn)).IsEquivalentTo(expected)
      .Because("the payload is the whole truth for the model: a removed perspective stops being cleaned for");
  }

  // ---------------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _cascadeAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand("SELECT cascade_delete_perspective_rows(@t, @ids)", conn);
    cmd.Parameters.AddWithValue("t", TABLE);
    cmd.Parameters.AddWithValue("ids", new[] { stream });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _reconcileAsync(
      NpgsqlConnection conn, bool withPerspectiveNames, string[]? names = null) {
    var perspectives = withPerspectiveNames ? names ?? [PERSPECTIVE_A, PERSPECTIVE_B] : [string.Empty];
    var entries = perspectives.Select(p => withPerspectiveNames
      ? $$"""{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}","PerspectiveName":"{{p}}"}"""
      : $$"""{"ClrTypeName":"{{MODEL}}","TableName":"{{TABLE}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}"}""");
    await using var cmd = new NpgsqlCommand("SELECT * FROM reconcile_perspective_registry(@p::jsonb, @svc)", conn);
    cmd.Parameters.AddWithValue("p", "[" + string.Join(",", entries) + "]");
    cmd.Parameters.AddWithValue("svc", SERVICE);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<string>> _perspectiveNamesAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand(
      "SELECT unnest(perspective_names) FROM wh_perspective_registry WHERE clr_type_name = @m AND service_name = @s", conn);
    cmd.Parameters.AddWithValue("m", MODEL);
    cmd.Parameters.AddWithValue("s", SERVICE);
    var names = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  private static async Task _arrangeAsync(
      NpgsqlConnection conn, int? ttlSeconds, int? cap, bool acknowledged = true, bool withPerspectiveNames = true) {
    await _executeAsync(conn, $@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE table_name = '{TABLE}';");

    await _reconcileAsync(conn, withPerspectiveNames);

    await using var enroll = new NpgsqlCommand(
      "UPDATE wh_perspective_registry SET row_retention_enrolled = TRUE, " +
      "retention_enforcement_acknowledged = @ack, row_ttl_seconds = @ttl, row_cap_per_scope = @cap " +
      "WHERE table_name = @t", conn);
    enroll.Parameters.AddWithValue("ack", acknowledged);
    enroll.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)ttlSeconds ?? DBNull.Value
    });
    enroll.Parameters.Add(new NpgsqlParameter(nameof(cap), NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)cap ?? DBNull.Value
    });
    enroll.Parameters.AddWithValue("t", TABLE);
    await enroll.ExecuteNonQueryAsync();
  }

  private static async Task<(Guid Evicted, Guid Survivor)> _seedTwoStreamsAsync(
      NpgsqlConnection conn, int evictedUpdatedDaysAgo, int survivorUpdatedDaysAgo) {
    var evicted = Guid.CreateVersion7();
    var survivor = Guid.CreateVersion7();
    await _seedRowAsync(conn, evicted, evictedUpdatedDaysAgo, expiresInHours: null);
    await _seedRowAsync(conn, survivor, survivorUpdatedDaysAgo, expiresInHours: null);
    await _seedCompanionsAsync(conn, evicted);
    await _seedCompanionsAsync(conn, survivor);
    return (evicted, survivor);
  }

  /// <summary>
  /// Seeds a row whose business times and stamped expiry are all relative to the DATABASE clock, the same
  /// NOW() the sweeps compare against, so host and server clock skew cannot move a row across a boundary.
  /// </summary>
  private static async Task _seedRowAsync(NpgsqlConnection conn, Guid id, int updatedDaysAgo, int? expiresInHours) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version, expires_at)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb,
              NOW() - make_interval(days => @d), NOW() - make_interval(days => @d), 1,
              NOW() + make_interval(hours => @h))", conn);
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("d", updatedDaysAgo);
    cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)expiresInHours ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// For one stream: a cursor and three snapshots for each perspective of the model, and the same for a
  /// perspective of ANOTHER model that also reads the stream.
  /// </summary>
  private static async Task _seedCompanionsAsync(NpgsqlConnection conn, Guid stream) {
    foreach (var name in new[] { PERSPECTIVE_A, PERSPECTIVE_B, OTHER_PERSPECTIVE }) {
      await using var cursor = new NpgsqlCommand(
        "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, status, processed_at) VALUES (@s, @n, 0, NOW())", conn);
      cursor.Parameters.AddWithValue("s", stream);
      cursor.Parameters.AddWithValue("n", name);
      await cursor.ExecuteNonQueryAsync();
      for (var i = 1; i <= 3; i++) {
        await _seedSnapshotAsync(conn, stream, name, sequence: i, version: i * 10);
      }
    }
  }

  private static async Task<Guid> _seedSnapshotAsync(
      NpgsqlConnection conn, Guid stream, string perspective, long sequence, int? version) {
    var snapshotEventId = Guid.CreateVersion7();
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
      "VALUES (@s, @n, @e, '{}'::jsonb, @q, @v)", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    cmd.Parameters.AddWithValue("e", snapshotEventId);
    cmd.Parameters.AddWithValue("q", sequence);
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)version ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
    return snapshotEventId;
  }

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue(nameof(id), id);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string table, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT COUNT(*) FROM {table} WHERE stream_id = @s AND perspective_name = @n", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<List<Guid>> _snapshotIdsAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      "SELECT snapshot_event_id FROM wh_perspective_snapshots WHERE stream_id = @s AND perspective_name = @n", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<int?> _snapshotVersionAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      "SELECT snapshot_version FROM wh_perspective_snapshots WHERE stream_id = @s AND perspective_name = @n", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("n", perspective);
    var value = await cmd.ExecuteScalarAsync();
    return value is int v ? v : null;
  }

  private static async Task _assertCleanedAsync(NpgsqlConnection conn, Guid evicted, Guid survivor) {
    await Assert.That(await _rowExistsAsync(conn, evicted)).IsFalse()
      .Because("the arrangement must actually evict the row, or nothing below means anything");
    await Assert.That(await _rowExistsAsync(conn, survivor)).IsTrue();

    foreach (var name in new[] { PERSPECTIVE_A, PERSPECTIVE_B }) {
      await Assert.That(await _countAsync(conn, "wh_perspective_cursors", evicted, name)).IsEqualTo(0)
        .Because($"{name} folds the evicted model: its cursor goes, so the next event rebuilds the row");
      await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", evicted, name)).IsEqualTo(1)
        .Because($"{name} keeps exactly one snapshot of the evicted row: the floor the rebuild restores from");
      await Assert.That(await _snapshotVersionAsync(conn, evicted, name)).IsEqualTo(30)
        .Because("the kept snapshot is the latest one, not an arbitrary one");

      await Assert.That(await _countAsync(conn, "wh_perspective_cursors", survivor, name)).IsEqualTo(1)
        .Because("a row that was not evicted keeps its cursor");
      await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", survivor, name)).IsEqualTo(3)
        .Because("a row that was not evicted keeps every snapshot");
    }

    await Assert.That(await _countAsync(conn, "wh_perspective_cursors", evicted, OTHER_PERSPECTIVE)).IsEqualTo(1)
      .Because("a perspective of another model on the same stream did not lose its row");
    await Assert.That(await _countAsync(conn, "wh_perspective_snapshots", evicted, OTHER_PERSPECTIVE)).IsEqualTo(3);
  }
}
