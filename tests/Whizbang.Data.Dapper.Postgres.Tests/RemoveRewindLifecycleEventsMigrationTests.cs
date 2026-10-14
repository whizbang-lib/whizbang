// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #1360: builds before the fix appended <c>PerspectiveRewindStarted</c> / <c>PerspectiveRewindCompleted</c>
/// into the application stream they rewound. Migration 210 removes them, with their bodies, and leaves the
/// version gaps: appends take the stream's highest version plus one, and renumbering would invalidate the
/// stream versions snapshots record (#1355). A rewind event's own stream (only lifecycle events) is left
/// alone, an event something still references is kept, and the pass is gated on a <c>wh_settings</c> marker
/// so it runs once.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/210_RemoveRewindLifecycleEventsFromApplicationStreams.sql</code-under-test>
[Category("Integration")]
public class RemoveRewindLifecycleEventsMigrationTests : IAsyncDisposable {
  private const string MIGRATION = "210_RemoveRewindLifecycleEventsFromApplicationStreams";
  private const string MARKER = "rewind_lifecycle_event_cleanup_version";
  private const string STARTED = "Whizbang.Core.Events.System.PerspectiveRewindStarted, Whizbang.Core";
  private const string COMPLETED = "Whizbang.Core.Events.System.PerspectiveRewindCompleted, Whizbang.Core";
  private const string APP = "Acme.Contracts.OrderContracts+UpdatedEvent, Acme.Contracts";
  private const string EMPTY_JSON = "'{}'::jsonb";

  private string? _testDatabaseName;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    }.ConnectionString;
    await new PostgresSchemaInitializer(_connectionString).InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName is null) {
      return;
    }
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
    } catch (NpgsqlException) {
      // teardown only; a leftover test database does not affect the outcome
    }
    _testDatabaseName = null;
    _connectionString = null;
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  [Test]
  public async Task FreshInit_RecordsTheCleanupAsDoneAsync() {
    // A new database has nothing to clean; the marker still records that the pass ran.
    await Assert.That(await _settingAsync()).IsEqualTo("1");
  }

  [Test]
  public async Task Migration210_RemovesRewindEventsFromApplicationStreams_AndLeavesTheGapsAsync() {
    var app = Guid.NewGuid();
    var failed = Guid.NewGuid();
    var own = Guid.NewGuid();
    await _resetMarkerAsync();
    // An application stream: a rewind's Started/Completed pair sits between two application events.
    var a1 = await _appendAsync(app, 1, APP);
    var a2 = await _appendAsync(app, 2, STARTED);
    var a3 = await _appendAsync(app, 3, COMPLETED);
    var a4 = await _appendAsync(app, 4, APP);
    // A failed rewind: Started went out, Completed never did. The cleanup must not rely on pairs.
    var f1 = await _appendAsync(failed, 1, APP);
    var f2 = await _appendAsync(failed, 2, STARTED);
    // A rewind event's own stream, as the fixed build writes it: nothing but lifecycle events.
    var o1 = await _appendAsync(own, 1, STARTED);
    var o2 = await _appendAsync(own, 2, COMPLETED);

    await _runMigrationAsync();

    await Assert.That(await _eventsAsync(app)).IsEquivalentTo(new[] { (a1, 1), (a4, 4) })
      .Because("the lifecycle events go, and the application events keep their versions: the gap is left");
    await Assert.That(await _eventsAsync(failed)).IsEquivalentTo(new[] { (f1, 1) });
    await Assert.That(await _eventsAsync(own)).IsEquivalentTo(new[] { (o1, 1), (o2, 2) })
      .Because("a rewind event's own stream is where the fixed build writes them");
    await Assert.That(await _bodiesAsync(a2, a3, f2)).IsEqualTo(0L).Because("a removed event's body goes with it");
    await Assert.That(await _bodiesAsync(a1, a4, f1, o1, o2)).IsEqualTo(5L);
    await Assert.That(await _settingAsync()).IsEqualTo("1");
  }

  [Test]
  public async Task Migration210_KeepsAnEventSomethingStillReferencesAsync() {
    // Nothing references these on the environments measured, but a cursor or snapshot that did would
    // be left pointing at nothing (the cursor's foreign key refuses it outright). Keep it, do not force it.
    var stream = Guid.NewGuid();
    await _resetMarkerAsync();
    await _appendAsync(stream, 1, APP);
    var cursorTarget = await _appendAsync(stream, 2, STARTED);
    var snapshotTarget = await _appendAsync(stream, 3, COMPLETED);
    var free = await _appendAsync(stream, 4, STARTED);
    await _execAsync($"""
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      VALUES ('{stream}', 'Test.Projection', '{cursorTarget}', 0);
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, created_at)
      VALUES ('{stream}', 'Test.Projection', '{snapshotTarget}', {EMPTY_JSON}, 1, NOW());
      """);

    await _runMigrationAsync();

    var left = (await _eventsAsync(stream)).Select(e => e.EventId).ToList();
    await Assert.That(left).Contains(cursorTarget);
    await Assert.That(left).Contains(snapshotTarget);
    await Assert.That(left).DoesNotContain(free);
  }

  [Test]
  public async Task Migration210_RunsOnce_AndAnAppendAfterItContinuesFromTheTopAsync() {
    var stream = Guid.NewGuid();
    await _resetMarkerAsync();
    await _appendAsync(stream, 1, APP);
    await _appendAsync(stream, 2, STARTED);
    await _runMigrationAsync();

    // Marked done: a lifecycle event in an application stream written after the pass is not removed by a
    // re-run, which is the marker doing its job (the fixed build never writes one there).
    var late = await _appendAsync(stream, 3, COMPLETED);
    await _runMigrationAsync();
    await Assert.That((await _eventsAsync(stream)).Select(e => e.EventId)).Contains(late);

    // Appends take MAX(version) + 1, so the gap at 2 is never refilled and never collides.
    var next = await _scalarAsync<int>($"SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = '{stream}'");
    await Assert.That(next).IsEqualTo(4);
  }

  private async Task<Guid> _appendAsync(Guid stream, int version, string eventType) {
    var id = Guid.NewGuid();
    await _execAsync($"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES ('{id}', '{stream}', '{stream}', 'Test', '{eventType}', {version}, NOW());
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES ('{id}', {EMPTY_JSON}, {EMPTY_JSON});
      """);
    return id;
  }

  private async Task<List<(Guid EventId, int Version)>> _eventsAsync(Guid stream) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    var rows = await conn.QueryAsync<(Guid, int)>(
      "SELECT event_id, version FROM wh_event_store WHERE stream_id = @S ORDER BY version", new { S = stream });
    return [.. rows];
  }

  private Task<long> _bodiesAsync(params Guid[] ids) =>
    _scalarAsync<long>($"SELECT count(*) FROM wh_event_body WHERE event_id = ANY(ARRAY[{string.Join(",", ids.Select(i => $"'{i}'::uuid"))}])");

  private Task<string?> _settingAsync() =>
    _scalarAsync<string?>($"SELECT setting_value FROM wh_settings WHERE setting_key = '{MARKER}'");

  private Task _resetMarkerAsync() => _execAsync($"DELETE FROM wh_settings WHERE setting_key = '{MARKER}'");

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    return (await conn.ExecuteScalarAsync<T>(sql))!;
  }

  private async Task _execAsync(string sql) {
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync(sql);
  }

  /// <summary>Applies the migration the way both drivers do: plain segments once, batched regions until they report zero.</summary>
  private async Task _runMigrationAsync() {
    var sql = new PostgresMigrationProvider().GetMigration(MIGRATION)?.Sql
      ?? throw new InvalidOperationException($"migration {MIGRATION} is not shipped");
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    foreach (var segment in MigrationBatchRegions.Segment(sql)) {
      if (segment.Kind == MigrationSegmentKind.Plain) {
        await conn.ExecuteAsync(segment.Sql);
        continue;
      }
      var passes = 0;
      while ((await conn.ExecuteScalarAsync<long?>(segment.Sql) ?? 0L) > 0) {
        if (++passes > 10_000) {
          throw new InvalidOperationException("a batched region kept reporting rows without finishing");
        }
      }
    }
  }
}
