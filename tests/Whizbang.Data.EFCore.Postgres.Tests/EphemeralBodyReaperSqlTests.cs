using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 073's tier-1 consumption-gated ephemeral body reaper — Task 8 of
/// <c>perform_maintenance</c>. <c>wh_event_body</c> holds ONLY ephemeral bodies (offloaded by the emit
/// chain, migration 072). The reaper deletes a body once every perspective that consumes its event has
/// processed it — i.e. no unprocessed <c>wh_perspective_events</c> work item still references it — leaving
/// the <c>wh_event_store</c> pointer in place (body-NULL is the deterministic rebuild-guard signal). It is
/// skipped entirely under <c>debug_mode</c> so retained forensic bodies survive alongside retained work
/// items. Verified against a real Postgres so the migration SQL (check_function_bodies=on) runs end-to-end.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralBodyReaperSqlTests : EFCoreTestBase {
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

  private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection connection, string eventType, string perspectiveName) {
    await using var assoc = connection.CreateCommand();
    assoc.CommandText = @"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING";
    assoc.Parameters.AddWithValue("t", eventType);
    assoc.Parameters.AddWithValue("p", perspectiveName);
    await assoc.ExecuteNonQueryAsync();
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid instanceId, Guid eventId, Guid streamId, string eventType, int flags) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(instanceId, eventId, streamId, eventType, flags));
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task _runMaintenanceAsync(NpgsqlConnection connection) {
    await using var m = connection.CreateCommand();
    m.CommandText = "SELECT * FROM perform_maintenance()";
    await using var r = await m.ExecuteReaderAsync();
    while (await r.ReadAsync()) { /* drain the task result rows */ }
  }

  private static async Task<long> _eventBodyCountAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT count(*) FROM wh_event_body WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (long)(await v.ExecuteScalarAsync())!;
  }

  // The rewind grace window defaults to 300s; these consumption-gate tests set it to 0 so a consumed body
  // reaps immediately (the grace window itself is covered by its own test).
  private static async Task _setGraceSecondsAsync(NpgsqlConnection connection, int seconds) {
    await using var g = connection.CreateCommand();
    g.CommandText = "UPDATE wh_settings SET setting_value = @v WHERE setting_key = 'ephemeral_rewind_grace_seconds'";
    g.Parameters.AddWithValue("v", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await g.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Reap_ConsumedEphemeralBody_HeldByGraceWindow_ReapedOnlyWhenAgedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    // No association => no perspective work item => "consumed"; only the grace window gates it.
    await _commitAsync(connection, Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.GraceWindowEvent", flags: 8);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L).Because("The ephemeral body is offloaded.");

    // Recently consumed but within the default 300s grace window => retained for out-of-order rewind.
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("A recently-consumed ephemeral body is held by the rewind grace window so an out-of-order straggler can still rewind through it.");

    // Age it past the grace window.
    await using (var age = connection.CreateCommand()) {
      age.CommandText = "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '10 minutes' WHERE event_id = @id";
      age.Parameters.AddWithValue("id", eventId);
      await age.ExecuteNonQueryAsync();
    }
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Once older than the grace window, the consumed body is reaped.");
  }

  [Test]
  public async Task Reap_PerTypeGraceOverride_ReapsAtItsOwnWindowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventA = Guid.NewGuid();
    var eventB = Guid.NewGuid();
    // Both consumed (no association) and recent. Type A gets a 0s per-type override; type B uses the global.
    await _commitAsync(connection, Guid.NewGuid(), eventA, Guid.NewGuid(), "Whizbang.Tests.GraceOverrideA", flags: 8);
    await _commitAsync(connection, Guid.NewGuid(), eventB, Guid.NewGuid(), "Whizbang.Tests.GraceOverrideB", flags: 8);

    await using (var s = connection.CreateCommand()) {
      s.CommandText = "SELECT sync_ephemeral_type_grace(ARRAY['Whizbang.Tests.GraceOverrideA']::text[], ARRAY[0]::integer[])";
      await s.ExecuteNonQueryAsync();
    }

    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventA)).IsEqualTo(0L)
      .Because("Type A's 0s per-type grace override reaps its consumed body immediately.");
    await Assert.That(await _eventBodyCountAsync(connection, eventB)).IsEqualTo(1L)
      .Because("Type B has no override, so the global 300s grace holds its recently-consumed body.");
  }

  [Test]
  public async Task Reap_ConsumedBody_HeldUntilSnapshotCoversAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // isolate the coverage gate from the grace window

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.CoverageGateEvent";
    await _seedPerspectiveAssociationAsync(connection, eventType, "CoverPerspective");
    await _commitAsync(connection, Guid.NewGuid(), eventId, streamId, eventType, flags: 8);

    // Consume + stamp commit_sequence, but NO snapshot yet.
    await using (var c = connection.CreateCommand()) {
      c.CommandText = "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id; " +
        "UPDATE wh_event_store SET commit_sequence = 3 WHERE event_id = @id";
      c.Parameters.AddWithValue("id", eventId);
      await c.ExecuteNonQueryAsync();
    }

    // Consumed + aged (grace 0) but UNCOVERED (no snapshot floor) => held by the coverage gate.
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("The coverage gate holds a consumed body until the consuming perspective has a snapshot floor — the reap must never outrun the rewind floor.");

    // Add a covering snapshot at/past the event's commit_sequence => now reapable.
    await using (var snap = connection.CreateCommand()) {
      snap.CommandText = "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence) " +
        "VALUES (@sid, 'CoverPerspective', @eid, '{}'::jsonb, 1, 3)";
      snap.Parameters.AddWithValue("sid", streamId);
      snap.Parameters.AddWithValue("eid", eventId);
      await snap.ExecuteNonQueryAsync();
    }
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Once the perspective has a snapshot at/past the event's commit_sequence, the body is reaped.");
  }

  [Test]
  public async Task Reap_EphemeralBody_GatedWhilePending_ReapedAfterConsumedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // this test covers the consumption gate, not grace

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.PresencePingEvent";

    // A consuming perspective => the emit chain creates a wh_perspective_events work item for the event.
    await _seedPerspectiveAssociationAsync(connection, eventType, "PresencePerspective");
    await _commitAsync(connection, Guid.NewGuid(), eventId, streamId, eventType, flags: 8);

    // Sanity: body offloaded, and an UNPROCESSED work item references it.
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("The ephemeral body is offloaded to wh_event_body by the emit chain.");
    await using (var pe = connection.CreateCommand()) {
      pe.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_id = @id AND processed_at IS NULL";
      pe.Parameters.AddWithValue("id", eventId);
      await Assert.That((long)(await pe.ExecuteScalarAsync())!).IsEqualTo(1L)
        .Because("An unprocessed perspective work item gates the body.");
    }

    // Phase 1: the perspective has NOT consumed yet => maintenance must NOT reap the body (consumption gate).
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("The body is consumption-gated — it survives while a perspective work item is still unprocessed.");

    // Consume: the perspective processes the event (its work item is marked processed).
    await using (var consume = connection.CreateCommand()) {
      consume.CommandText = "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id";
      consume.Parameters.AddWithValue("id", eventId);
      await consume.ExecuteNonQueryAsync();
    }

    // The coverage gate also requires a snapshot FLOOR for the consuming perspective (in production the
    // reap-driven step drives this just before the reap). Stamp commit_sequence + a covering snapshot.
    await using (var stamp = connection.CreateCommand()) {
      stamp.CommandText = "UPDATE wh_event_store SET commit_sequence = 7 WHERE event_id = @id";
      stamp.Parameters.AddWithValue("id", eventId);
      await stamp.ExecuteNonQueryAsync();
    }
    await using (var snap = connection.CreateCommand()) {
      snap.CommandText = "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence) " +
        "VALUES (@sid, 'PresencePerspective', @eid, '{}'::jsonb, 1, 7)";
      snap.Parameters.AddWithValue("sid", streamId);
      snap.Parameters.AddWithValue("eid", eventId);
      await snap.ExecuteNonQueryAsync();
    }

    // Phase 2: every consumer is done AND snapshot-covered => maintenance reaps the body.
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Once every consuming perspective has processed the event, the ephemeral body is reaped.");

    // The pointer stays — a pointer with no body row is the deterministic rebuild-guard signal, not a lost event.
    await using (var ptr = connection.CreateCommand()) {
      ptr.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
      ptr.Parameters.AddWithValue("id", eventId);
      await Assert.That((long)(await ptr.ExecuteScalarAsync())!).IsEqualTo(1L)
        .Because("The wh_event_store pointer row survives the reap; only the wh_event_body row is deleted.");
    }
  }

  [Test]
  public async Task Reap_DebugMode_RetainsConsumedEphemeralBodyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.TypingIndicatorEvent";
    await _seedPerspectiveAssociationAsync(connection, eventType, "TypingPerspective");
    await _commitAsync(connection, Guid.NewGuid(), eventId, Guid.NewGuid(), eventType, flags: 8);

    // Fully consumed …
    await using (var consume = connection.CreateCommand()) {
      consume.CommandText = "UPDATE wh_perspective_events SET processed_at = NOW() WHERE event_id = @id";
      consume.Parameters.AddWithValue("id", eventId);
      await consume.ExecuteNonQueryAsync();
    }
    // … but debug_mode retains forensic rows, so the reap must be skipped.
    await using (var dbg = connection.CreateCommand()) {
      dbg.CommandText = "UPDATE wh_settings SET setting_value = 'true' WHERE setting_key = 'debug_mode'";
      await dbg.ExecuteNonQueryAsync();
    }

    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("Under debug_mode the reaper is skipped so the ephemeral body is retained for forensics, like the completed work items.");
  }

  [Test]
  public async Task Reap_NoConsumingPerspective_EphemeralBodyReapedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // this test covers the consumption gate, not grace

    var eventId = Guid.NewGuid();
    // No association => the emit chain creates NO perspective work item => nothing consumes the event.
    await _commitAsync(connection, Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.OrphanEphemeralEvent", flags: 8);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("The ephemeral body is offloaded even when no perspective consumes it.");

    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("With no consuming perspective there is nothing to wait for — the body is reapable immediately.");
  }

  // Commits an ephemeral event whose EnvelopeMetadata carries the raw TTL ('ett'), exercising the full
  // metadata-carry path: request Metadata -> wh_outbox.metadata -> emit chain materialises
  // wh_event_body.metadata.ephemeral_expires_at = created_at + ttl (migration 080). Null ttl => WhenConsumed
  // (no 'ett' key, so no expiry key).
  private static async Task _commitWithTtlAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, int? ttlSeconds) {
    var ett = ttlSeconds is null ? "" : $"\"ett\": {ttlSeconds.Value}";
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": { {{ett}} }, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task _ageAsync(NpgsqlConnection connection, Guid eventId, string interval) {
    await using var age = connection.CreateCommand();
    age.CommandText = $"UPDATE wh_event_store SET created_at = NOW() - INTERVAL '{interval}' WHERE event_id = @id";
    age.Parameters.AddWithValue("id", eventId);
    await age.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _bodyExpiryAsync(NpgsqlConnection connection, Guid eventId) {
    await using var v = connection.CreateCommand();
    v.CommandText = "SELECT metadata->>'ephemeral_expires_at' FROM wh_event_body WHERE event_id = @id";
    v.Parameters.AddWithValue("id", eventId);
    return (string?)await v.ExecuteScalarAsync();
  }

  private static async Task _setBodyExpiryAsync(NpgsqlConnection connection, Guid eventId, DateTimeOffset expiry) {
    await using var u = connection.CreateCommand();
    u.CommandText = "UPDATE wh_event_body SET metadata = jsonb_set(metadata, '{ephemeral_expires_at}', to_jsonb(@ts::text)) WHERE event_id = @id";
    u.Parameters.AddWithValue("id", eventId);
    u.Parameters.AddWithValue("ts", expiry.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    await u.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Reap_AfterTtl_StampedExpiry_HeldUntilPast_ThenReapedAsync() {
    // E2-4c: an AfterTtl event carries its own absolute expiry in its metadata (stamped at dispatch =
    // emit-time + TtlSeconds, lifted into wh_event_body.metadata by migration 080). It EXTENDS retention past
    // consumption: the consumed, aged, snapshot-covered body is kept until it is ALSO past that expiry.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // isolate the TTL floor from the grace window

    var eventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.ChatMessageTtl";
    // No association => consumed + vacuously covered. A 1-hour TTL => expires_at = created_at + 1h (future).
    await _commitWithTtlAsync(connection, eventId, Guid.NewGuid(), eventType, 3600);

    // Migration 080: the emit chain materialised created_at + ttl into the body metadata.
    await Assert.That(await _bodyExpiryAsync(connection, eventId)).IsNotNull()
      .Because("The emit chain materialises created_at + ett into wh_event_body.metadata.ephemeral_expires_at.");

    // Aged past the (0s) grace window but NOT past its expiry => the TTL floor holds it.
    await _ageAsync(connection, eventId, "10 minutes");
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("An AfterTtl body is retained until past its stamped expiry, even once consumed and aged past grace.");

    // Its expiry passes => now reapable.
    await _setBodyExpiryAsync(connection, eventId, DateTimeOffset.UtcNow.AddMinutes(-1));
    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(0L)
      .Because("Once past its expiry, the consumed AfterTtl body is reaped.");
  }

  [Test]
  public async Task Reap_ExpiryKeyIsTheDiscriminator_AfterTtlHeldWhileWhenConsumedReapsAsync() {
    // The presence of an 'ephemeral_expires_at' key in the body metadata is what marks an event age-gated
    // (AfterTtl) rather than consumption-gated (WhenConsumed). Two sibling events, identical except the key.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);

    var afterTtl = Guid.NewGuid();
    var whenConsumed = Guid.NewGuid();
    await _commitWithTtlAsync(connection, afterTtl, Guid.NewGuid(), "Whizbang.Tests.AgeGatedEvent", 3600);
    await _commitWithTtlAsync(connection, whenConsumed, Guid.NewGuid(), "Whizbang.Tests.ConsumeGatedEvent", ttlSeconds: null);

    // Both consumed (no association) and aged 10 min past the 0s grace.
    await _ageAsync(connection, afterTtl, "10 minutes");
    await _ageAsync(connection, whenConsumed, "10 minutes");
    await _runMaintenanceAsync(connection);

    await Assert.That(await _eventBodyCountAsync(connection, whenConsumed)).IsEqualTo(0L)
      .Because("A WhenConsumed event (no ephemeral_expires_at) reaps as soon as it is consumed and aged past grace.");
    await Assert.That(await _eventBodyCountAsync(connection, afterTtl)).IsEqualTo(1L)
      .Because("The sibling AfterTtl event (future stamped expiry) is held by its TTL floor under identical conditions.");
  }

  [Test]
  public async Task Reap_SourcedBodyInBodyTable_IsNeverReapedAsync() {
    // #13b4 safety gate. The full split moves SOURCED bodies into wh_event_body too, breaking the
    // "wh_event_body holds ONLY ephemeral bodies by construction" invariant the reaper leaned on. The reap
    // must be explicitly gated on the event being ephemeral (flags&8) — a sourced body in the body table is
    // the durable log and must NEVER be reaped, no matter how consumed/aged/covered it is.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // fully aged; no association => consumed + vacuously covered

    var eventId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.SourcedSplitBody", flags: 0);

    // Full split (077): the sourced body lives in wh_event_body from birth.
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L);

    await _runMaintenanceAsync(connection);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("A SOURCED body row is the durable log — the reaper's ephemeral gate (flags&8) must never touch it, even when consumed, aged past grace, and snapshot-covered.");
  }

  [Test]
  public async Task Reap_SourcedEvent_BodyRowUntouchedByMaintenanceAsync() {
    // Full split (077): a Sourced event's body lives in wh_event_body from birth. Maintenance must never
    // touch it — the reaper's ephemeral gate (flags&8, #13b4-0) is what protects the durable log.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _setGraceSecondsAsync(connection, 0);   // even fully aged, sourced is never reap-eligible

    var eventId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.OrderPlacedEvent", flags: 0);
    await Assert.That(await _eventBodyCountAsync(connection, eventId)).IsEqualTo(1L)
      .Because("Post-077 the sourced body is offloaded to wh_event_body at emit time.");

    await _runMaintenanceAsync(connection);

    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT (event_data->>'OrderId') FROM wh_event_body WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var body = (string?)await v.ExecuteScalarAsync();
      await Assert.That(body).IsEqualTo("42")
        .Because("Maintenance never deletes a Sourced event's body — the reaper is gated on flags&8.");
    }
  }
}
