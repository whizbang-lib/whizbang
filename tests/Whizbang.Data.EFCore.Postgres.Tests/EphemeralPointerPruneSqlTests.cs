using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 076's tier-2 deep-maintenance pointer prune —
/// <c>prune_ancient_ephemeral_pointers()</c>. The tier-1 reaper (073) deletes consumed, aged ephemeral
/// BODIES but leaves the <c>wh_event_store</c> pointer as the #13d rebuild-guard signal. Tier-2 optionally
/// prunes those ANCIENT pointers (bodies already reaped, past the horizon, no pending work) for storage
/// economy — while KEEPING the newest pointer per stream, so both the rebuild guard (<c>flags&amp;8</c>) and
/// the perspective cursor's <c>last_event_id</c> target survive. It is DISABLED by default, self-gated to a
/// monthly interval, and skipped under <c>debug_mode</c>. Verified against a real Postgres so the migration
/// SQL (check_function_bodies=on) runs end-to-end.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralPointerPruneSqlTests : EFCoreTestBase {
  private static string _commitRequest(Guid instanceId, Guid eventId, Guid streamId, string eventType, int flags) => $$"""
    {
      "instance_id": "{{instanceId}}",
      "service_name": "test",
      "host_name": "test-host",
      "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}",
        "Destination": "out-topic",
        "MessageType": "{{eventType}}",
        "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": {{flags}}
      }]
    }
    """;

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, int flags) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(Guid.NewGuid(), eventId, streamId, eventType, flags));
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  // Simulate the tier-1 reap having already deleted the ephemeral body (pointer-only, body-NULL).
  private static Task _reapBodyAsync(NpgsqlConnection connection, Guid eventId) =>
    _execAsync(connection, "DELETE FROM wh_event_body WHERE event_id = @id", ("id", eventId));

  private static Task _agePointerAsync(NpgsqlConnection connection, Guid eventId, int days) =>
    _execAsync(connection,
      $"UPDATE wh_event_store SET created_at = NOW() - INTERVAL '{days.ToString(CultureInfo.InvariantCulture)} days' WHERE event_id = @id",
      ("id", eventId));

  private static Task _enableDeepMaintenanceAsync(NpgsqlConnection connection, bool enabled) =>
    _execAsync(connection,
      "UPDATE wh_settings SET setting_value = @v WHERE setting_key = 'ephemeral_deep_maintenance_enabled'",
      ("v", enabled ? "true" : "false"));

  private static Task _setDebugModeAsync(NpgsqlConnection connection, bool on) =>
    _execAsync(connection,
      "INSERT INTO wh_settings (setting_key, setting_value, value_type, description) VALUES ('debug_mode', @v, 'boolean', 't') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value",
      ("v", on ? "true" : "false"));

  private static async Task<(long Rows, string Status)> _pruneAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT rows_pruned, status FROM prune_ancient_ephemeral_pointers()";
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetInt64(0), r.GetString(1));
  }

  private static async Task<long> _pointerCountAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  private static async Task<List<int>> _remainingVersionsAsync(NpgsqlConnection connection, Guid streamId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT version FROM wh_event_store WHERE stream_id = @sid ORDER BY version";
    v.Parameters.AddWithValue("sid", streamId);
    var versions = new List<int>();
    await using var r = await v.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      versions.Add(r.GetInt32(0));
    }
    return versions;
  }

  [Test]
  public async Task Prune_KeepsNewestPointerPerStream_PrunesOlderReapedEphemeralPointersAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.PrunePresence";
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    var e3 = Guid.NewGuid();
    // Three ephemeral events on one stream => versions 1, 2, 3.
    await _commitAsync(connection, e1, streamId, eventType, flags: 8);
    await _commitAsync(connection, e2, streamId, eventType, flags: 8);
    await _commitAsync(connection, e3, streamId, eventType, flags: 8);

    // Tier-1 has already reaped every body; age every pointer well past the 90-day horizon.
    foreach (var e in new[] { e1, e2, e3 }) {
      await _reapBodyAsync(connection, e);
      await _agePointerAsync(connection, e, 200);
    }

    await _enableDeepMaintenanceAsync(connection, true);
    var (rows, status) = await _pruneAsync(connection);

    await Assert.That(status).IsEqualTo("ok");
    await Assert.That(rows).IsEqualTo(2L).Because("The two older pointers prune; the newest is kept as the stream tombstone.");
    var remaining = await _remainingVersionsAsync(connection, streamId);
    await Assert.That(remaining.Count).IsEqualTo(1)
      .Because("Only the newest pointer survives for the stream.");
    await Assert.That(remaining[0]).IsEqualTo(3)
      .Because("The surviving pointer is version 3 — the rebuild-guard tombstone and cursor last_event_id target.");

    // The surviving pointer keeps the stream flagged ephemeral, so the rebuild guard still detects it.
    await using var flagCmd = connection.CreateCommand();
    flagCmd.CommandText = "SELECT count(*) FROM wh_event_store WHERE stream_id = @sid AND (flags & 8) = 8";
    flagCmd.Parameters.AddWithValue("sid", streamId);
    await Assert.That((long)(await flagCmd.ExecuteScalarAsync())!).IsEqualTo(1L)
      .Because("The kept tombstone keeps flags&8 present so GetStateBasedStreamIdsAsync still refuses a rebuild.");
  }

  [Test]
  public async Task Prune_DisabledByDefault_IsNoOpAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _commitAsync(connection, e1, streamId, "Whizbang.Tests.PruneDisabled", flags: 8);
    await _commitAsync(connection, e2, streamId, "Whizbang.Tests.PruneDisabled", flags: 8);
    foreach (var e in new[] { e1, e2 }) {
      await _reapBodyAsync(connection, e);
      await _agePointerAsync(connection, e, 200);
    }

    // ephemeral_deep_maintenance_enabled defaults to false — never touched.
    var (rows, status) = await _pruneAsync(connection);
    await Assert.That(status).IsEqualTo("disabled");
    await Assert.That(rows).IsEqualTo(0L);
    await Assert.That(await _pointerCountAsync(connection, e1)).IsEqualTo(1L)
      .Because("With deep maintenance disabled (the default), ancient ephemeral pointers are never pruned.");
  }

  [Test]
  public async Task Prune_BodyNotYetReaped_IsKeptAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var older = Guid.NewGuid();
    var newer = Guid.NewGuid();
    await _commitAsync(connection, older, streamId, "Whizbang.Tests.PruneBodyLive", flags: 8);
    await _commitAsync(connection, newer, streamId, "Whizbang.Tests.PruneBodyLive", flags: 8);
    // Age both; reap ONLY the newer's body. The older (version 1) still has its body => not prune-eligible,
    // even though it is ancient and not the newest.
    await _agePointerAsync(connection, older, 200);
    await _agePointerAsync(connection, newer, 200);
    await _reapBodyAsync(connection, newer);

    await _enableDeepMaintenanceAsync(connection, true);
    var (rows, _) = await _pruneAsync(connection);
    await Assert.That(rows).IsEqualTo(0L)
      .Because("A pointer whose body is not yet reaped is never pruned — tier-2 only removes tombstones tier-1 already emptied.");
    await Assert.That(await _pointerCountAsync(connection, older)).IsEqualTo(1L);
  }

  [Test]
  public async Task Prune_WithinHorizon_IsKeptAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var older = Guid.NewGuid();
    var newer = Guid.NewGuid();
    await _commitAsync(connection, older, streamId, "Whizbang.Tests.PruneRecent", flags: 8);
    await _commitAsync(connection, newer, streamId, "Whizbang.Tests.PruneRecent", flags: 8);
    // Reap both bodies but keep the older recent (only 5 days old — well within the 90-day horizon).
    await _reapBodyAsync(connection, older);
    await _reapBodyAsync(connection, newer);
    await _agePointerAsync(connection, older, 5);
    await _agePointerAsync(connection, newer, 5);

    await _enableDeepMaintenanceAsync(connection, true);
    var (rows, _) = await _pruneAsync(connection);
    await Assert.That(rows).IsEqualTo(0L)
      .Because("A reaped pointer younger than the horizon is retained — it may still be needed by a cross-service replay or dedup window.");
    await Assert.That(await _pointerCountAsync(connection, older)).IsEqualTo(1L);
  }

  [Test]
  public async Task Prune_PendingPerspectiveWork_IsKeptAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.PrunePendingWork";
    // A consuming perspective association => the emit chain creates a perspective work item.
    await _execAsync(connection,
      "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
      "VALUES (gen_random_uuid(), @t, 'perspective', 'PendingP', 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING",
      ("t", eventType));

    var older = Guid.NewGuid();
    var newer = Guid.NewGuid();
    await _commitAsync(connection, older, streamId, eventType, flags: 8);
    await _commitAsync(connection, newer, streamId, eventType, flags: 8);
    // Reap + age the older, but leave its perspective work item UNPROCESSED (pending).
    await _reapBodyAsync(connection, older);
    await _agePointerAsync(connection, older, 200);
    await _reapBodyAsync(connection, newer);
    await _agePointerAsync(connection, newer, 200);

    await _enableDeepMaintenanceAsync(connection, true);
    var (rows, _) = await _pruneAsync(connection);
    await Assert.That(rows).IsEqualTo(0L)
      .Because("A pointer with an unprocessed perspective work item is never pruned, even when ancient.");
    await Assert.That(await _pointerCountAsync(connection, older)).IsEqualTo(1L);
  }

  [Test]
  public async Task Prune_SourcedPointer_IsNeverPrunedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var s1 = Guid.NewGuid();
    var s2 = Guid.NewGuid();
    // Sourced events (flags 0): their bodies live inline in wh_event_store (no wh_event_body row), so the
    // "body reaped" NOT EXISTS is vacuously true — the flags&8 gate is the only thing keeping them safe.
    await _commitAsync(connection, s1, streamId, "Whizbang.Tests.PruneSourced", flags: 0);
    await _commitAsync(connection, s2, streamId, "Whizbang.Tests.PruneSourced", flags: 0);
    await _agePointerAsync(connection, s1, 400);
    await _agePointerAsync(connection, s2, 400);

    await _enableDeepMaintenanceAsync(connection, true);
    var (rows, _) = await _pruneAsync(connection);
    await Assert.That(rows).IsEqualTo(0L)
      .Because("Sourced pointers are the durable log — the flags&8 gate ensures the prune never touches them.");
    await Assert.That(await _pointerCountAsync(connection, s1)).IsEqualTo(1L);
  }

  [Test]
  public async Task Prune_SelfGate_SecondImmediateCallIsNotDueAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    var e3 = Guid.NewGuid();
    await _commitAsync(connection, e1, streamId, "Whizbang.Tests.PruneSelfGate", flags: 8);
    await _commitAsync(connection, e2, streamId, "Whizbang.Tests.PruneSelfGate", flags: 8);
    await _commitAsync(connection, e3, streamId, "Whizbang.Tests.PruneSelfGate", flags: 8);
    foreach (var e in new[] { e1, e2, e3 }) {
      await _reapBodyAsync(connection, e);
      await _agePointerAsync(connection, e, 200);
    }
    await _enableDeepMaintenanceAsync(connection, true);

    var first = await _pruneAsync(connection);
    await Assert.That(first.Status).IsEqualTo("ok");
    await Assert.That(first.Rows).IsEqualTo(2L);

    // Immediately calling again: the 30-day self-gate interval has not elapsed since the first run.
    var second = await _pruneAsync(connection);
    await Assert.That(second.Status).IsEqualTo("not due");
    await Assert.That(second.Rows).IsEqualTo(0L)
      .Because("The self-gate advances the last-run watermark, so the heavy prune runs at most once per interval.");
  }

  [Test]
  public async Task Coordinator_PruneAncientEphemeralPointers_InvokesSqlAndParsesResultAsync() {
    // #13b3b plumbing: the EFCore coordinator override calls the SQL function and surfaces (rows, status).
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    // Fresh DB => the opt-in flag is at its default (false).
    var whenDisabled = await coordinator.PruneAncientEphemeralPointersAsync();
    await Assert.That(whenDisabled.Status).IsEqualTo("disabled");
    await Assert.That(whenDisabled.RowsPruned).IsEqualTo(0L);

    // Enable + seed a stream with two ancient, reaped ephemeral pointers => one prunes, newest kept.
    var streamId = Guid.NewGuid();
    var older = Guid.NewGuid();
    var newer = Guid.NewGuid();
    await _commitAsync(connection, older, streamId, "Whizbang.Tests.PruneViaCoordinator", flags: 8);
    await _commitAsync(connection, newer, streamId, "Whizbang.Tests.PruneViaCoordinator", flags: 8);
    foreach (var e in new[] { older, newer }) {
      await _reapBodyAsync(connection, e);
      await _agePointerAsync(connection, e, 200);
    }
    await _enableDeepMaintenanceAsync(connection, true);

    var whenEnabled = await coordinator.PruneAncientEphemeralPointersAsync();
    await Assert.That(whenEnabled.Status).IsEqualTo("ok");
    await Assert.That(whenEnabled.RowsPruned).IsEqualTo(1L)
      .Because("The coordinator path prunes the older pointer and keeps the newest tombstone, same as the raw SQL.");
  }

  [Test]
  public async Task Prune_DebugMode_IsSkippedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _commitAsync(connection, e1, streamId, "Whizbang.Tests.PruneDebug", flags: 8);
    await _commitAsync(connection, e2, streamId, "Whizbang.Tests.PruneDebug", flags: 8);
    foreach (var e in new[] { e1, e2 }) {
      await _reapBodyAsync(connection, e);
      await _agePointerAsync(connection, e, 200);
    }
    await _enableDeepMaintenanceAsync(connection, true);
    await _setDebugModeAsync(connection, true);

    var (rows, status) = await _pruneAsync(connection);
    await Assert.That(status).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(rows).IsEqualTo(0L)
      .Because("debug_mode retains forensic rows, so the pointer prune is skipped entirely — like the tier-1 reaper.");
    await Assert.That(await _pointerCountAsync(connection, e1)).IsEqualTo(1L);
  }
}
