// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2145: the emit chain creates a <c>__collective__</c> row for a stored collective event only when this service has a
/// handler for its type, once the startup associations step has written the service's <c>collective</c> associations and
/// its <c>collective_gate</c> marker. Without a marker (an older instance's registration, a mixed-version fleet, or a
/// handled set the service could not determine) every collective event still gets its row, so a gap in what the gate
/// knows can only create work, never drop it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/391_CollectiveRowsOnlyForHandledTypes.sql</code-under-test>
/// <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
[Category("Integration")]
[Category("Shard2")]
public class CollectiveRowGateSqlTests : EFCoreTestBase {
  private const string SINK = "__collective__";
  private const string SERVICE = "Jobs.Service";
  private const string HANDLED = "Contracts.ArchiveJobsCollective, Contracts";
  private const string UNHANDLED = "Contracts.OverlayAppliedCollective, Contracts";

  [Test]
  public async Task Outbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync() {
    // The mixed-version fleet: the migration is applied but no instance of this version has registered yet (or an older
    // instance re-registered after it and removed the marker). Nothing is gated.
    await using var conn = await _openAsync();
    var unhandled = await _emitOutboxAsync(conn, UNHANDLED);

    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(1L);
  }

  [Test]
  public async Task Outbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync() {
    await using var conn = await _openAsync();
    await _registerGateAsync(conn, SERVICE, HANDLED);

    var handled = await _emitOutboxAsync(conn, HANDLED);
    var unhandled = await _emitOutboxAsync(conn, UNHANDLED);

    await Assert.That(await _sinkRowsAsync(conn, handled)).IsEqualTo(1L)
      .Because("a type a [CollectiveApplyFor] handler on this service applies is queued as before");
    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(0L)
      .Because("no handler here applies it, so a row would only be claimed and completed for nothing");
    await Assert.That(await _storedAsync(conn, unhandled)).IsTrue().Because("the event itself is still stored");
  }

  [Test]
  public async Task Outbox_AMarkerWithNoHandledType_GatesEveryCollectiveEventAsync() {
    await using var conn = await _openAsync();
    await _registerGateAsync(conn, SERVICE);

    var unhandled = await _emitOutboxAsync(conn, UNHANDLED);

    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(0L);
  }

  [Test]
  public async Task Outbox_AnotherServiceInTheSchemaWithoutAMarker_KeepsEveryRowAsync() {
    // A schema two services register into: one gates, the other (an older release) does not say what it handles. Its
    // handlers must not lose their work to the first service's answer.
    await using var conn = await _openAsync();
    await _registerGateAsync(conn, SERVICE, HANDLED);
    await _registerAsync(conn, "Contracts.Ordinary, Contracts", "perspective", "Other.Perspective", "Other.Service");

    var unhandled = await _emitOutboxAsync(conn, UNHANDLED);

    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(1L);
  }

  [Test]
  public async Task Inbox_OnceTheMarkerExists_AHandledTypeGetsItsRow_AndAnUnhandledTypeGetsNoneAsync() {
    await using var conn = await _openAsync();
    await _registerGateAsync(conn, SERVICE, HANDLED);
    var instance = Guid.NewGuid();
    var handled = await _seedInboxAsync(conn, HANDLED, instance);
    var unhandled = await _seedInboxAsync(conn, UNHANDLED, instance);

    await _emitInboxAsync(conn, instance);

    await Assert.That(await _sinkRowsAsync(conn, handled)).IsEqualTo(1L);
    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(0L);
    await Assert.That(await _storedAsync(conn, unhandled)).IsTrue();
  }

  [Test]
  public async Task Inbox_WithoutAGateMarker_EveryCollectiveEventGetsItsRowAsync() {
    await using var conn = await _openAsync();
    var instance = Guid.NewGuid();
    var unhandled = await _seedInboxAsync(conn, UNHANDLED, instance);

    await _emitInboxAsync(conn, instance);

    await Assert.That(await _sinkRowsAsync(conn, unhandled)).IsEqualTo(1L);
  }

  // ---------- helpers ----------

  /// <summary>
  /// A connection to a schema with no association yet: the template's own perspective associations belong to a service
  /// that writes no gate marker, which (correctly) keeps the gate off, so each case registers exactly the services it
  /// is about.
  /// </summary>
  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var clear = conn.CreateCommand();
    clear.CommandText = "DELETE FROM wh_message_associations";
    await clear.ExecuteNonQueryAsync();
    return conn;
  }

  /// <summary>What the startup associations step writes for a service whose handled set is known.</summary>
  private static async Task _registerGateAsync(NpgsqlConnection conn, string service, params string[] handled) {
    foreach (var type in handled) {
      await _registerAsync(conn, type, "collective", SINK, service);
    }
    await _registerAsync(conn, SINK, "collective_gate", SINK, service);
  }

  private static async Task _registerAsync(NpgsqlConnection conn, string type, string kind, string target, string service) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @type, @kind, @target, @service, @type, NOW(), NOW())
      """;
    cmd.Parameters.AddWithValue(nameof(type), type);
    cmd.Parameters.AddWithValue(nameof(kind), kind);
    cmd.Parameters.AddWithValue(nameof(target), target);
    cmd.Parameters.AddWithValue(nameof(service), service);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _emitOutboxAsync(NpgsqlConnection conn, string eventType) {
    var id = Guid.NewGuid();
    var instance = Guid.NewGuid();
    await using (var insert = conn.CreateCommand()) {
      insert.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
           1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, 1, NOW())
        """;
      insert.Parameters.AddWithValue(nameof(id), id);
      insert.Parameters.AddWithValue("type", eventType);
      insert.Parameters.AddWithValue("inst", instance);
      insert.Parameters.AddWithValue("sid", Guid.NewGuid());
      await insert.ExecuteNonQueryAsync();
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, new[] { id });
    emit.Parameters.AddWithValue("inst", instance);
    await emit.ExecuteNonQueryAsync();
    return id;
  }

  /// <summary>One collective inbox event leased to <paramref name="holder"/> and not yet through the store pass.</summary>
  private static async Task<Guid> _seedInboxAsync(NpgsqlConnection conn, string eventType, Guid holder) {
    var id = Guid.NewGuid();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority, flags)
        VALUES (@id, 'H', @type, '{"p": {}}', '{}', NOW() - INTERVAL '1 minute', @sid, true, 150, 1)
        RETURNING message_id, stream_id, received_at, priority, is_event)
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 0, @holder, NOW() + INTERVAL '5 minutes'
      FROM m
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("sid", Guid.NewGuid());
    cmd.Parameters.AddWithValue(nameof(holder), holder);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task _emitInboxAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _sinkRowsAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_perspective_events WHERE event_id = @eid AND perspective_name = @sink";
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sink", SINK);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _storedAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @eid)";
    cmd.Parameters.AddWithValue("eid", eventId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }
}
